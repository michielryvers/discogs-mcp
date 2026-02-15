# Discogs MCP Server

A Model Context Protocol (MCP) server that provides full access to the Discogs REST API. Distributed as a .NET tool via NuGet.

## Tools

| Tool                | Description                                           |
| ------------------- | ----------------------------------------------------- |
| `discogs_help`      | Usage guide with examples                             |
| `discogs_endpoints` | Discover API endpoints by category or keyword         |
| `discogs_request`   | Execute any Discogs API call (GET, POST, PUT, DELETE) |
| `discogs_paginate`  | Fetch multiple pages and concatenate results          |

## Setup

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Discogs API token](https://www.discogs.com/settings/developers)

### Installation

No installation required. MCP clients launch the tool on demand using `dnx`.

Add the server to your MCP client configuration:

**OpenCode** (`opencode.json`):

```json
{
  "mcp": {
    "discogs": {
      "type": "local",
      "enabled": true,
      "command": ["dnx", "discogs-mcp", "--yes"],
      "environment": {
        "DISCOGS_TOKEN": "your_token_here",
        "DISCOGS_USER_AGENT": "YourApp/0.1 (+https://yoursite.example)"
      }
    }
  }
}
```

**Claude Code** (via `claude mcp add`):

```bash
claude mcp add discogs -- dnx discogs-mcp --yes
```

Then set the required environment variables (`DISCOGS_TOKEN`).

**VS Code** (`.vscode/mcp.json`):

```json
{
  "servers": {
    "discogs": {
      "command": "dnx",
      "args": ["discogs-mcp", "--yes"],
      "env": {
        "DISCOGS_TOKEN": "your_token_here"
      }
    }
  }
}
```

### Environment Variables

| Variable             | Required | Description                                      |
| -------------------- | -------- | ------------------------------------------------ |
| `DISCOGS_TOKEN`      | Yes      | Your Discogs personal access token               |
| `DISCOGS_USER_AGENT` | No       | Custom User-Agent string (a default is provided) |

## Example Prompts

Once configured, you can ask your AI assistant things like:

**Collection analysis**

- "What's in my Discogs collection? Show me a summary by genre and decade."
- "Which artists appear most frequently in my collection?"

**Wantlist and deals**

- "Check my Discogs wantlist and find the cheapest listings on the marketplace for each item."
- "Look at my wantlist and tell me which items have the best deals right now."

**Discovery**

- "Search for jazz releases on Blue Note Records from the 1960s and recommend some classics."
- "Find releases similar to what's in my collection based on labels and styles."

**Price research**

- "What's the market value for 'Nirvana - Nevermind' on vinyl? Compare different pressings."

## API Categories

The server provides access to these Discogs API categories:

- **database** - Search, releases, masters, artists, labels
- **collection** - User collection folders and releases
- **wantlist** - User wantlist management
- **marketplace** - Listings, orders, price suggestions
- **user** - Profile, submissions, contributions
- **lists** - User-created lists
- **inventory** - Bulk inventory management

## Publishing

To publish a new version to NuGet:

```bash
# Pack the tool
dotnet pack -c Release

# Push to NuGet.org
dotnet nuget push ./nupkg/discogs-mcp.0.1.0.nupkg --api-key YOUR_API_KEY --source https://api.nuget.org/v3/index.json
```

## License

MIT
