# GitHub Issue Analysis System

This project demonstrates GitHub repository issue analysis using Orleans grains and AI to extract common themes and help development teams prioritize their work.

## Overview

The GitHub Issue Analysis system is designed to process GitHub issues from repositories, extract key themes as tags using AI, and generate summary reports with recommendations for development priorities.

## Agents

### GitHubAnalysisGAgent
- **Role**: Processes GitHub issues and coordinates the analysis workflow
- **Event Handlers**:
  - `HandleGitHubIssueEventAsync`: Processes issue data from a GitHub repository (title, description, labels, etc.)
  - Extracts tags from issues using LLM and publishes `IssueTagsEvent`
  - Periodically generates summary reports with prioritized recommendations using LLM and `SummaryReportEvent`

## Event Flow

1. User initiates analysis by specifying a GitHub repository
2. System fetches issues via the GitHub API 
3. `GitHubAnalysisGAgent` processes each issue and uses LLM to extract tags
4. After processing issues, the agent generates a summary report with AI-powered recommendations
5. System generates a `SummaryReportEvent` with actionable insights

## Key Features

- **AI-powered theme extraction**: Uses LLMs to identify key themes and tags from issue descriptions
- **Pattern recognition**: Identifies common problems and feature requests across multiple issues
- **Intelligent prioritization**: Uses AI to suggest development priorities based on issue patterns
- **Scalable analysis**: Can process repositories with large numbers of issues

## Implementation Details

The implementation uses the following components:

- **Orleans**: For distributed processing and state management
- **Octokit**: For GitHub API integration
- **Azure OpenAI**: For AI-powered analysis (optional, with fallback to static analysis)

## Running the Project

### Prerequisites

- .NET 9.0 SDK or later

### Configuration

Before running the application, you need to configure the following:

1. **LLM Configuration**: 
   To enable AI-powered analysis, update the Azure OpenAI API settings in `appsettings.json`:
   ```json
   "AzureOpenAI": {
     "ApiKey": "your-api-key",
     "Endpoint": "https://your-resource-name.openai.azure.com",
     "DeploymentName": "your-deployment-name",
     "ModelName": "gpt-35-turbo",
     "ApiVersion": "2024-02-15-preview"
   }
   ```
   
   Alternatively, you can use user secrets for development:
   ```
   dotnet user-secrets set "AzureOpenAI:ApiKey" "your-api-key"
   ```

   If you don't provide an API key, the system will use a fallback mode that uses static analysis instead of AI.

2. **GitHub Access Token (Optional)**:  
   To access private repositories or increase API rate limits, update the GitHub settings in `appsettings.json`:
   ```json
   "GitHub": {
     "PersonalAccessToken": "your-github-pat"
   }
   ```
   
   For security reasons, use user secrets or environment variables in production:
   ```
   dotnet user-secrets set "GitHub:PersonalAccessToken" "your-github-pat"
   ```

### Starting the Silo

1. Open a terminal and navigate to the GitHubIssueAnalysis.Silo project directory:
   ```
   cd src/Samples/GitHubIssueAnalysis/GitHubIssueAnalysis.Silo
   ```

2. Run the Silo:
   ```
   dotnet run
   ```

3. The Silo will start and listen for connections on localhost. The Orleans Dashboard will be available at http://localhost:8888.

### Running the Client

1. Open another terminal and navigate to the GitHubIssueAnalysis.Client project directory:
   ```
   cd src/Samples/GitHubIssueAnalysis/GitHubIssueAnalysis.Client
   ```

2. Run the client:
   ```
   dotnet run
   ```

3. Follow the on-screen instructions to analyze GitHub repository issues.

### Testing with Sample Repositories

You can test the analysis with these well-known repositories:
- microsoft/semantic-kernel
- dotnet/orleans
- microsoft/azureai-samples

## Technologies Used

- [.NET 9.0](https://dotnet.microsoft.com/)
- [Orleans 9.0](https://dotnet.github.io/orleans/)
- [Octokit](https://github.com/octokit/octokit.net) - GitHub API client
- [Azure OpenAI](https://azure.microsoft.com/en-us/products/ai-services/openai-service) - AI text analysis capabilities
- [Serilog](https://serilog.net/) - Structured logging 