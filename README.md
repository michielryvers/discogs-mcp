# Discogs MCP Server

A Model Context Protocol (MCP) server that provides full access to the Discogs REST API. Built as a single-file C# application.

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

1. Clone the repository:

   ```bash
   git clone https://github.com/michielryvers/discogs-mcp.git
   ```

2. Add the MCP server to your client configuration. For OpenCode, copy and edit the example config:

   ```bash
   cp opencode.example.json opencode.json
   ```

   Edit `opencode.json` with your Discogs token:

   ```json
   {
     "mcp": {
       "discogs": {
         "type": "local",
         "enabled": true,
         "command": ["dotnet", "run", "/path/to/discogs-mcp/discogs-mcp.cs"],
         "environment": {
           "DISCOGS_TOKEN": "your_token_here",
           "DISCOGS_USER_AGENT": "YourApp/0.1 (+https://yoursite.example)"
         }
       }
     }
   }
   ```

   For other MCP clients, configure the server to run `dotnet run discogs-mcp.cs` with the environment variables `DISCOGS_TOKEN` and `DISCOGS_USER_AGENT`.

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

## License

MIT
