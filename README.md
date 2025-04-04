# GitHub Issue Agent

A sample application that analyzes GitHub repository issues using AI, extracts common themes, and helps prioritize development work.

## Environment Setup

This application uses environment variables for sensitive configuration such as API keys. We use `.env` files to manage these variables locally, which are not committed to the repository for security reasons.

### Setting Up Environment Files

1. Create a `.env` file in the following directories:
   - `src/Samples/GitHubIssueAnalysis/GitHubIssueAnalysis.Client/`
   - `src/Samples/GitHubIssueAnalysis/GitHubIssueAnalysis.Silo/`

2. Example `.env` file for the client:
```
# GitHub API Configuration
GITHUB_PERSONAL_ACCESS_TOKEN=your_github_token_here
```

3. Example `.env` file for the silo:
```
# Google Gemini Configuration
GOOGLE_GEMINI_API_KEY=your_gemini_key_here
GOOGLE_GEMINI_MODEL=gemini-2.0-flash

# GitHub API Configuration
GITHUB_PERSONAL_ACCESS_TOKEN=your_github_token_here

# Use Fallback LLM when API keys are missing
USE_FALLBACK_LLM=true
```

### Important Security Notes

- Never commit `.env` files to your repository
- The `.env` files are included in `.gitignore` to prevent accidental commits
- If no API keys are provided, the application will use a fallback LLM service that provides basic responses

## Running the Application

1. Start the Silo server:
```
cd src/Samples/GitHubIssueAnalysis/GitHubIssueAnalysis.Silo/
dotnet run
```

2. In a separate terminal, start the Client:
```
cd src/Samples/GitHubIssueAnalysis/GitHubIssueAnalysis.Client/
dotnet run
```

3. Follow the prompts in the client to analyze GitHub repositories.

## Configuration

The application uses the following configuration sources in order of precedence:
1. Environment variables (from `.env` files or system environment)
2. appsettings.json
3. Default values

### LLM Services

The application is configured to use:
1. Google Gemini as the primary LLM service (when API key is provided)
2. A fallback service when no API keys are available or in case of errors

## Features

- Analyze open and closed issues from GitHub repositories
- Extract common themes and categorize issues
- Generate recommendations based on issue analysis
- Visualize issue statistics with percentage graphs
- Support for filtering issues by state (open/closed/all)

## Technical Architecture

The application uses:
- .NET for the application framework
- Orleans for distributed computing and actor model
- Memory streams for communication between components
- Serilog for structured logging
- Google Gemini API for AI analysis 