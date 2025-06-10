# Azure Repos Client CLI
> Wrapper around Azure Repos rest API

- [Getting Started](#getting-started)
- [Project Overview](#project-overview)

## Prerequisites

- Azure DevOps Personal Access Token (PAT) with code read permissions

## Getting Started

### Running the app

```
cd ./src/AzReposClient.Cli/
dotnet run -- --help
```

### Using Docker

The ./compose.development.yaml file is provided to support an entire development
experience with vim in docker

1. `cp ./.env.example .env`
2. `docker compose run --rm cli bash`

## Project Overview

The purpose of this project is to consume the Azure Repos commit rest API
because there is no OData connection for PowerBI. In addition, a Python script
in PowerBI only supports a Personal gateway, which is not useful for my needs.
Storing the results somewhere allows me to consume it from PowerBI.

## Contributing

Please open issues or pull requests. It is recommended to open an issue prior to
a pull request.

## License

[MIT License](LICENSE)
