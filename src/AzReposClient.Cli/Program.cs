using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Data.SqlClient;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Dapper;

// Define options
var patOption = new Option<string>("--pat", "Azure DevOps Personal Access Token") { IsRequired = true };
var organizationOption = new Option<string>("--organization", "Azure DevOps organization") { IsRequired = true };
var projectOption = new Option<string>("--project", "Azure DevOps project name") { IsRequired = true };
var queryParamsOption = new Option<string>("--queryParams", () => "", "Additional API query parameters");
var repoParserConfigOption = new Option<string>("--repoParserConfig", () => "", "Repo parser config, e.g. 'repo1=conventional,repo2=custom'");

// Output option with limited choices
var outputOption = new Option<string>("--output", () => "stdout", "Output destination: db, stdout, file");
outputOption.AddValidator(result =>
{
    var val = result.GetValueOrDefault<string>();
    var allowed = new[] { "db", "stdout", "file" };
    if (!Array.Exists(allowed, x => x.Equals(val, StringComparison.OrdinalIgnoreCase)))
        result.ErrorMessage = $"Invalid output '{val}'. Allowed: db, stdout, file";
});

// SQL connection string option (required if output=db)
var sqlConnectionStringOption = new Option<string>("--sqlConnectionString", "SQL connection string");

// Output file option (required if output=file)
var outputFileOption = new Option<string>("--outputFile", "Output file path");

// Root command
var rootCommand = new RootCommand("Azure DevOps Repo CLI");

// Add global options
rootCommand.AddGlobalOption(patOption);
rootCommand.AddGlobalOption(organizationOption);
rootCommand.AddGlobalOption(projectOption);
rootCommand.AddGlobalOption(queryParamsOption);
rootCommand.AddGlobalOption(repoParserConfigOption);
rootCommand.AddGlobalOption(outputOption);
rootCommand.AddGlobalOption(sqlConnectionStringOption);
rootCommand.AddGlobalOption(outputFileOption);

// commits command
var commitsCommand = new Command("commits", "Fetch commits from Azure DevOps and output/save");
commitsCommand.SetHandler(async (string pat, string org, string proj, string queryParams, string repoParserConfig,
                                string output, string sqlConnStr, string outputFile) =>
{
    // Validate output-dependent options
    if (output.Equals("db", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(sqlConnStr))
    {
        Console.Error.WriteLine("Error: --sqlConnectionString is required when --output=db");
        return;
    }
    if (output.Equals("file", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(outputFile))
    {
        Console.Error.WriteLine("Error: --outputFile is required when --output=file");
        return;
    }

    var repoParsers = ParseRepoParserConfig(repoParserConfig);
    var commitsData = await GetAzureDevOpsCommits(org, proj, pat, queryParams, repoParsers);

    switch (output.ToLowerInvariant())
    {
        case "db":
            await SaveToDatabase(commitsData, sqlConnStr);
            Console.WriteLine("Data inserted successfully into database.");
            break;

        case "file":
            await SaveToFile(commitsData, outputFile);
            Console.WriteLine($"Data written successfully to file '{outputFile}'.");
            break;

        case "stdout":
        default:
            PrintToStdOut(commitsData);
            break;
    }
},
patOption, organizationOption, projectOption, queryParamsOption, repoParserConfigOption,
outputOption, sqlConnectionStringOption, outputFileOption);

rootCommand.AddCommand(commitsCommand);

// Run the command line parser
return await rootCommand.InvokeAsync(args);

#region Helpers

static Dictionary<string, string> ParseRepoParserConfig(string config)
{
    var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    if (string.IsNullOrWhiteSpace(config))
        return dict;

    var pairs = config.Split(',', StringSplitOptions.RemoveEmptyEntries);
    foreach (var pair in pairs)
    {
        var kv = pair.Split('=', 2);
        if (kv.Length == 2)
        {
            dict[kv[0].Trim()] = kv[1].Trim().ToLowerInvariant();
        }
    }
    return dict;
}

static async Task<List<RepoData>> GetAzureDevOpsCommits(string organization, string projectName, string pat, string queryParams, Dictionary<string, string> repoParsers)
{
    using var client = new HttpClient();
    var authToken = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}"));
    client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", authToken);

    string reposUrl = $"https://dev.azure.com/{organization}/{projectName}/_apis/git/repositories?api-version=7.1";

    var reposResponse = await client.GetStringAsync(reposUrl);
    var reposJson = JObject.Parse(reposResponse);

    var repoDataList = new List<RepoData>();

    foreach (var repo in reposJson["value"])
    {
        bool isDisabled = repo["isDisabled"]?.Value<bool>() ?? false;
        string defaultBranch = repo["defaultBranch"]?.Value<string>();

        if (!string.IsNullOrEmpty(defaultBranch) && !isDisabled)
        {
            string branchName = defaultBranch.Replace("refs/heads/", "");
            string commitsUrl = $"https://dev.azure.com/{organization}/{projectName}/_apis/git/repositories/{repo["name"]}/commits?searchCriteria.itemVersion.version={branchName}&api-version=7.1";

            if (!string.IsNullOrEmpty(queryParams))
            {
                commitsUrl += "&" + queryParams;
            }

            var commitsResponse = await client.GetStringAsync(commitsUrl);
            var commitsJson = JObject.Parse(commitsResponse);

            var commitsList = new List<CommitData>();

            string repoName = repo["name"].Value<string>();
            string parserName = repoParsers.ContainsKey(repoName) ? repoParsers[repoName] : "conventional";

            ICommitParser parser = parserName switch
            {
                "conventional" => new ConventionalCommitParser(),
                "custom" => new CustomCommitParser(),
                _ => new ConventionalCommitParser(),
            };

            foreach (var commit in commitsJson["value"])
            {
                string comment = commit["comment"]?.Value<string>() ?? "";
                string commitId = commit["commitId"]?.Value<string>() ?? "";

                string resultType = (comment.StartsWith("feat") || comment.StartsWith("fix")) ? "Deployed" : "Integrated";

                var parts = parser.Parse(comment);

                commitsList.Add(new CommitData
                {
                    CommitId = commitId,
                    AuthorJson = JsonConvert.SerializeObject(commit["author"]),
                    ResultType = resultType,
                    Title = comment,
                    PartsJson = parts != null ? JsonConvert.SerializeObject(parts) : null
                });
            }

            repoDataList.Add(new RepoData
            {
                Repository = repoName,
                CommitCount = commitsJson["count"].Value<int>(),
                BranchName = branchName,
                Commits = commitsList
            });
        }
    }

    return repoDataList;
}

static async Task SaveToDatabase(List<RepoData> data, string connectionString)
{
    using var connection = new SqlConnection(connectionString);
    await connection.OpenAsync();

    foreach (var repoData in data)
    {
        foreach (var commit in repoData.Commits)
        {
            string sql = @"
                INSERT INTO Commits (Repository, CommitCount, BranchName, CommitId, AuthorJson, ResultType, Title, PartsJson)
                VALUES (@Repository, @CommitCount, @BranchName, @CommitId, @AuthorJson, @ResultType, @Title, @PartsJson)";

            await connection.ExecuteAsync(sql, new
            {
                Repository = repoData.Repository,
                CommitCount = repoData.CommitCount,
                BranchName = repoData.BranchName,
                CommitId = commit.CommitId,
                AuthorJson = commit.AuthorJson,
                ResultType = commit.ResultType,
                Title = commit.Title,
                PartsJson = commit.PartsJson
            });
        }
    }
}

static async Task SaveToFile(List<RepoData> data, string filePath)
{
    var json = JsonConvert.SerializeObject(data, Formatting.Indented);
    await File.WriteAllTextAsync(filePath, json);
}

static void PrintToStdOut(List<RepoData> data)
{
    var json = JsonConvert.SerializeObject(data, Formatting.Indented);
    Console.WriteLine(json);
}

#endregion

#region Parsers and Models

interface ICommitParser
{
    object Parse(string commitMessage);
}

class ConventionalCommitParser : ICommitParser
{
    private static readonly Regex Pattern = new Regex(@"^(?<type>\w+)(?:\((?<scope>[^)]+)\))?: (?<description>.+)$", RegexOptions.Compiled);

    public object Parse(string commitMessage)
    {
        if (string.IsNullOrEmpty(commitMessage))
            return null;

        var match = Pattern.Match(commitMessage);
        if (!match.Success)
            return null;

        return new ConventionalCommitParts
        {
            Type = match.Groups["type"].Value,
            Scope = match.Groups["scope"].Success ? match.Groups["scope"].Value : null,
            Description = match.Groups["description"].Value
        };
    }
}

class CustomCommitParser : ICommitParser
{
    public object Parse(string commitMessage)
    {
        return new { RawMessage = commitMessage };
    }
}

class RepoData
{
    public string Repository { get; set; }
    public int CommitCount { get; set; }
    public string BranchName { get; set; }
    public List<CommitData> Commits { get; set; }
}

class CommitData
{
    public string CommitId { get; set; }
    public string AuthorJson { get; set; }
    public string ResultType { get; set; }
    public string Title { get; set; }
    public string PartsJson { get; set; }
}

class ConventionalCommitParts
{
    public string Type { get; set; }
    public string Scope { get; set; }
    public string Description { get; set; }
}

#endregion
