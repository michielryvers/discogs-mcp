using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

// ─── Bootstrap ───────────────────────────────────────────────────────────────

var token = Environment.GetEnvironmentVariable("DISCOGS_TOKEN")
    ?? throw new InvalidOperationException("DISCOGS_TOKEN environment variable is required.");

var userAgent = Environment.GetEnvironmentVariable("DISCOGS_USER_AGENT")
    ?? "discogs-mcp/0.1 (+https://github.com/discogs-mcp)";

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddConsole(opts =>
{
    opts.LogToStandardErrorThreshold = LogLevel.Trace;
});

builder.Services.AddHttpClient("discogs", client =>
{
    client.BaseAddress = new Uri("https://api.discogs.com");
    client.DefaultRequestHeaders.Authorization =
        new AuthenticationHeaderValue("Discogs", $"token={token}");
    client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
    client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.discogs.v2+json");
});

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new() { Name = "discogs-mcp", Version = "0.1.0" };
        options.ServerInstructions =
            "Discogs MCP server. Use discogs_help to learn how to use this server. " +
            "Use discogs_endpoints to discover API endpoints, then discogs_request to call them. " +
            "Use discogs_paginate for multi-page results.";
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();

// ─── Tools ───────────────────────────────────────────────────────────────────

[McpServerToolType]
public static class DiscogsTools
{
    // ── discogs_help ─────────────────────────────────────────────────────

    [McpServerTool(Name = "discogs_help"),
     Description("Return a short guide on how to use this Discogs MCP server, with examples.")]
    public static string Help()
    {
        return """
            # Discogs MCP Server — Quick Start

            This server gives you full access to the Discogs REST API via three tools:

            ## Tools

            ### 1. `discogs_endpoints` — Discover API endpoints
            Browse the full endpoint catalog. Filter by category or keyword.
            ```json
            { "category": "database" }
            { "q": "search" }
            { "category": "marketplace", "q": "listing", "includeExamples": true }
            ```

            ### 2. `discogs_request` — Call any Discogs endpoint
            Execute a single API call. The server handles auth and rate limiting.
            ```json
            { "method": "GET", "path": "/database/search", "query": { "q": "Nirvana Nevermind", "type": "release", "per_page": "5" } }
            { "method": "GET", "path": "/releases/249504" }
            { "method": "GET", "path": "/users/{your_username}/collection/folders" }
            { "method": "PUT", "path": "/users/{your_username}/wants/249504", "body": { "notes": "Classic!", "rating": 5 } }
            ```

            ### 3. `discogs_paginate` — Iterate over paginated results
            Fetches multiple pages and concatenates items. Specify `extract` to pull the items array.
            ```json
            {
              "method": "GET",
              "path": "/database/search",
              "query": { "q": "Blue Note", "type": "release" },
              "page": 1, "perPage": 50, "maxPages": 3,
              "extract": "results"
            }
            ```

            ## Workflow patterns
            - **Discover → Execute**: Use `discogs_endpoints` to find the right path, then `discogs_request`.
            - **Browse collections**: `discogs_paginate` with `extract` set to the items key (e.g. "releases", "wants", "results").
            - **Recommendations**: Paginate collection → extract styles/labels → search for similar.
            - **Deal finder**: Search marketplace → compare prices → filter bargains.

            ## Notes
            - Auth is handled automatically (personal access token).
            - All paths start with `/` (e.g. `/releases/249504`).
            - Query params are key-value string pairs.
            - Rate limiting: the server respects Discogs rate limits (60 req/min for authenticated users).
            """;
    }

    // ── discogs_endpoints ────────────────────────────────────────────────

    [McpServerTool(Name = "discogs_endpoints"),
     Description("Return a curated list of Discogs API endpoints. Filter by category (database, collection, wantlist, lists, marketplace, user, inventory) and/or keyword search.")]
    public static string Endpoints(
        [Description("Filter by category: database, collection, wantlist, lists, marketplace, user, inventory")]
        string? category = null,
        [Description("Keyword search across endpoint paths and descriptions")]
        string? q = null,
        [Description("Include example request payloads (default true)")]
        bool includeExamples = true)
    {
        var endpoints = EndpointCatalog.All;

        if (!string.IsNullOrWhiteSpace(category))
        {
            endpoints = endpoints
                .Where(e => e.Category.Equals(category, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            var kw = q.ToLowerInvariant();
            endpoints = endpoints
                .Where(e =>
                    e.Path.ToLowerInvariant().Contains(kw) ||
                    e.Summary.ToLowerInvariant().Contains(kw) ||
                    e.Methods.Any(m => m.ToLowerInvariant().Contains(kw)))
                .ToList();
        }

        if (endpoints.Count == 0)
            return "No endpoints matched your filters.";

        var sb = new StringBuilder();
        sb.AppendLine($"# Discogs API Endpoints ({endpoints.Count} results)\n");

        string? lastCategory = null;
        foreach (var ep in endpoints)
        {
            if (ep.Category != lastCategory)
            {
                sb.AppendLine($"## {ep.Category}\n");
                lastCategory = ep.Category;
            }

            sb.AppendLine($"### `{string.Join(" | ", ep.Methods)}` `{ep.Path}`");
            sb.AppendLine(ep.Summary);
            if (ep.Params.Length > 0)
                sb.AppendLine($"**Params**: {string.Join(", ", ep.Params)}");
            if (includeExamples && ep.Example != null)
            {
                sb.AppendLine("**Example**:");
                sb.AppendLine($"```json\n{JsonSerializer.Serialize(ep.Example, JsonOpts.Indented)}\n```");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    // ── discogs_request ──────────────────────────────────────────────────

    [McpServerTool(Name = "discogs_request"),
     Description("Call any Discogs REST API endpoint. The server handles authentication, User-Agent, and rate-limit headers. Returns status, headers, and parsed JSON (or raw text).")]
    public static async Task<string> Request(
        IHttpClientFactory httpClientFactory,
        [Description("HTTP method: GET, POST, PUT, DELETE, PATCH")]
        string method,
        [Description("API path starting with / (e.g. /database/search, /releases/249504)")]
        string path,
        [Description("Query parameters as key-value pairs (optional)")]
        Dictionary<string, string>? query = null,
        [Description("JSON body for write calls (optional)")]
        JsonElement? body = null,
        [Description("Accept header override (optional, defaults to application/vnd.discogs.v2+json)")]
        string? accept = null)
    {
        var client = httpClientFactory.CreateClient("discogs");
        var result = await ExecuteDiscogsRequest(client, method, path, query, body, accept);
        return JsonSerializer.Serialize(result, JsonOpts.Indented);
    }

    // ── discogs_paginate ─────────────────────────────────────────────────

    [McpServerTool(Name = "discogs_paginate"),
     Description("Fetch multiple pages from a paginated Discogs endpoint and return concatenated items. Specify the 'extract' key to pull items from each page's response (e.g. 'results', 'releases', 'wants', 'listings').")]
    public static async Task<string> Paginate(
        IHttpClientFactory httpClientFactory,
        [Description("HTTP method (usually GET)")]
        string method,
        [Description("API path starting with /")]
        string path,
        [Description("Base query parameters (optional, page/per_page managed automatically)")]
        Dictionary<string, string>? query = null,
        [Description("Starting page number (default 1)")]
        int page = 1,
        [Description("Items per page (default 50)")]
        int perPage = 50,
        [Description("Maximum number of pages to fetch (default 5, max 10)")]
        int maxPages = 5,
        [Description("JSON key containing the items array in each response (e.g. 'results', 'releases', 'wants', 'listings')")]
        string? extract = null)
    {
        maxPages = Math.Clamp(maxPages, 1, 10);
        perPage = Math.Clamp(perPage, 1, 100);

        var client = httpClientFactory.CreateClient("discogs");
        var allItems = new List<JsonElement>();
        int pagesFetched = 0;
        int? lastPage = null;
        int? nextPage = null;

        var baseQuery = query != null
            ? new Dictionary<string, string>(query)
            : new Dictionary<string, string>();

        for (int p = page; p < page + maxPages; p++)
        {
            baseQuery["page"] = p.ToString();
            baseQuery["per_page"] = perPage.ToString();

            var result = await ExecuteDiscogsRequest(client, method, path, baseQuery, null, null);
            pagesFetched++;

            if (!result.Ok)
            {
                // Stop pagination on error, but include what we have so far
                break;
            }

            if (result.Json.HasValue)
            {
                var json = result.Json.Value;

                // Try to extract pagination info
                if (json.TryGetProperty("pagination", out var pagination))
                {
                    if (pagination.TryGetProperty("pages", out var pages))
                        lastPage = pages.GetInt32();
                }

                // Extract items
                if (extract != null && json.TryGetProperty(extract, out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in items.EnumerateArray())
                        allItems.Add(item.Clone());
                }
                else if (json.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in json.EnumerateArray())
                        allItems.Add(item.Clone());
                }
                else
                {
                    // Can't extract, just add the whole response
                    allItems.Add(json.Clone());
                }

                // Check if we've reached the last page
                if (lastPage.HasValue && p >= lastPage.Value)
                    break;
            }
        }

        nextPage = lastPage.HasValue && (page + pagesFetched - 1) < lastPage.Value
            ? page + pagesFetched
            : null;

        var output = new
        {
            pagesFetched,
            totalItems = allItems.Count,
            pagination = new
            {
                nextPage = nextPage,
                lastPage = lastPage,
                perPage
            },
            items = allItems
        };

        return JsonSerializer.Serialize(output, JsonOpts.Indented);
    }

    // ── Shared HTTP execution ────────────────────────────────────────────

    private static async Task<DiscogsResponse> ExecuteDiscogsRequest(
        HttpClient client,
        string method,
        string path,
        Dictionary<string, string>? query,
        JsonElement? body,
        string? accept)
    {
        // Build URI — tolerate query strings embedded in path (LLMs commonly do this)
        if (!path.StartsWith('/'))
            path = "/" + path;

        var qsIndex = path.IndexOf('?');
        if (qsIndex >= 0)
        {
            var embeddedQs = path[(qsIndex + 1)..];
            path = path[..qsIndex];
            query ??= new Dictionary<string, string>();
            foreach (var pair in embeddedQs.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eqIndex = pair.IndexOf('=');
                if (eqIndex >= 0)
                {
                    var key = Uri.UnescapeDataString(pair[..eqIndex]);
                    var value = Uri.UnescapeDataString(pair[(eqIndex + 1)..]);
                    query.TryAdd(key, value); // explicit query dict wins over embedded
                }
            }
        }

        var uriBuilder = new UriBuilder(client.BaseAddress!) { Path = path };

        if (query is { Count: > 0 })
        {
            var qs = string.Join("&", query.Select(kv =>
                $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
            uriBuilder.Query = qs;
        }

        var httpMethod = method.ToUpperInvariant() switch
        {
            "GET" => HttpMethod.Get,
            "POST" => HttpMethod.Post,
            "PUT" => HttpMethod.Put,
            "DELETE" => HttpMethod.Delete,
            "PATCH" => HttpMethod.Patch,
            _ => throw new ArgumentException($"Unsupported HTTP method: {method}")
        };

        var request = new HttpRequestMessage(httpMethod, uriBuilder.Uri);

        if (accept != null)
            request.Headers.Accept.ParseAdd(accept);

        if (body.HasValue && httpMethod != HttpMethod.Get)
        {
            request.Content = new StringContent(
                body.Value.GetRawText(),
                Encoding.UTF8,
                "application/json");
        }

        // Execute with basic retry for 429
        HttpResponseMessage response;
        int retries = 0;
        const int maxRetries = 3;

        while (true)
        {
            response = await client.SendAsync(request.Clone());

            if (response.StatusCode == HttpStatusCode.TooManyRequests && retries < maxRetries)
            {
                retries++;
                var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5 * retries);
                await Task.Delay(retryAfter);

                // Rebuild request since it was consumed
                request = new HttpRequestMessage(httpMethod, uriBuilder.Uri);
                if (accept != null)
                    request.Headers.Accept.ParseAdd(accept);
                if (body.HasValue && httpMethod != HttpMethod.Get)
                {
                    request.Content = new StringContent(
                        body.Value.GetRawText(),
                        Encoding.UTF8,
                        "application/json");
                }
                continue;
            }

            break;
        }

        // Parse response
        var responseText = await response.Content.ReadAsStringAsync();

        // Gather useful headers
        var headers = new Dictionary<string, string>();
        foreach (var name in new[] {
            "X-Discogs-Ratelimit",
            "X-Discogs-Ratelimit-Used",
            "X-Discogs-Ratelimit-Remaining" })
        {
            if (response.Headers.TryGetValues(name, out var vals))
                headers[name] = string.Join(", ", vals);
        }

        JsonElement? json = null;
        string? text = null;

        if (response.Content.Headers.ContentType?.MediaType?.Contains("json") == true)
        {
            try
            {
                json = JsonSerializer.Deserialize<JsonElement>(responseText);
            }
            catch
            {
                text = responseText;
            }
        }
        else
        {
            text = responseText;
        }

        return new DiscogsResponse
        {
            Ok = response.IsSuccessStatusCode,
            Status = (int)response.StatusCode,
            Headers = headers,
            Json = json,
            Text = text
        };
    }
}

// ─── Request cloning extension ───────────────────────────────────────────────

public static class HttpRequestMessageExtensions
{
    public static HttpRequestMessage Clone(this HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        if (request.Content != null)
        {
            var content = request.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            clone.Content = new ByteArrayContent(content);
            if (request.Content.Headers.ContentType != null)
                clone.Content.Headers.ContentType = request.Content.Headers.ContentType;
        }
        foreach (var header in request.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return clone;
    }
}

// ─── Response model ──────────────────────────────────────────────────────────

public class DiscogsResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("status")]
    public int Status { get; set; }

    [JsonPropertyName("headers")]
    public Dictionary<string, string> Headers { get; set; } = new();

    [JsonPropertyName("json")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Json { get; set; }

    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }
}

// ─── JSON serializer options ─────────────────────────────────────────────────

public static class JsonOpts
{
    public static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}

// ─── Endpoint catalog ────────────────────────────────────────────────────────

public record EndpointEntry(
    string Category,
    string Path,
    string[] Methods,
    string Summary,
    string[] Params,
    object? Example = null);

public static class EndpointCatalog
{
    public static readonly List<EndpointEntry> All = new()
    {
        // ── Database ─────────────────────────────────────────────────────

        new("database", "/database/search", new[] { "GET" },
            "Search the Discogs database. Requires authentication.",
            new[] { "q", "type", "title", "release_title", "credit", "artist", "anv", "label", "genre", "style", "country", "year", "format", "catno", "barcode", "track", "submitter", "contributor", "page", "per_page" },
            new { method = "GET", path = "/database/search", query = new { q = "Nirvana Nevermind", type = "release", per_page = "5" } }),

        new("database", "/releases/{release_id}", new[] { "GET" },
            "Get a release (a particular physical or digital object released).",
            new[] { "curr_abbr" },
            new { method = "GET", path = "/releases/249504" }),

        new("database", "/releases/{release_id}/rating/{username}", new[] { "GET", "PUT", "DELETE" },
            "Get, update, or delete a user's rating for a release.",
            Array.Empty<string>(),
            new { method = "PUT", path = "/releases/249504/rating/my_username", body = new { rating = 4 } }),

        new("database", "/releases/{release_id}/rating", new[] { "GET" },
            "Get the community release rating (average and count).",
            Array.Empty<string>()),

        new("database", "/releases/{release_id}/stats", new[] { "GET" },
            "Get release statistics (have/want counts).",
            Array.Empty<string>()),

        new("database", "/masters/{master_id}", new[] { "GET" },
            "Get a master release.",
            Array.Empty<string>(),
            new { method = "GET", path = "/masters/1000" }),

        new("database", "/masters/{master_id}/versions", new[] { "GET" },
            "Get all versions (releases) of a master release. Paginated.",
            new[] { "page", "per_page", "format", "label", "released", "country", "sort", "sort_order" },
            new { method = "GET", path = "/masters/1000/versions", query = new { per_page = "10" } }),

        new("database", "/artists/{artist_id}", new[] { "GET" },
            "Get an artist.",
            Array.Empty<string>(),
            new { method = "GET", path = "/artists/108713" }),

        new("database", "/artists/{artist_id}/releases", new[] { "GET" },
            "Get an artist's releases. Paginated.",
            new[] { "sort", "sort_order", "page", "per_page" },
            new { method = "GET", path = "/artists/108713/releases", query = new { sort = "year", sort_order = "desc" } }),

        new("database", "/labels/{label_id}", new[] { "GET" },
            "Get a label, company, recording studio, or other entity.",
            Array.Empty<string>(),
            new { method = "GET", path = "/labels/1" }),

        new("database", "/labels/{label_id}/releases", new[] { "GET" },
            "Get all releases for a label. Paginated.",
            new[] { "page", "per_page" },
            new { method = "GET", path = "/labels/1/releases", query = new { per_page = "10" } }),

        // ── User Identity ────────────────────────────────────────────────

        new("user", "/oauth/identity", new[] { "GET" },
            "Get basic info about the authenticated user (sanity check).",
            Array.Empty<string>(),
            new { method = "GET", path = "/oauth/identity" }),

        new("user", "/users/{username}", new[] { "GET", "POST" },
            "Get or edit a user profile. POST requires authentication as that user.",
            new[] { "name", "home_page", "location", "profile", "curr_abbr" },
            new { method = "GET", path = "/users/rodneyfool" }),

        new("user", "/users/{username}/submissions", new[] { "GET" },
            "Get a user's submissions (edits to releases/labels/artists). Paginated.",
            new[] { "page", "per_page" }),

        new("user", "/users/{username}/contributions", new[] { "GET" },
            "Get a user's contributions (items they submitted). Paginated.",
            new[] { "sort", "sort_order", "page", "per_page" }),

        // ── Collection ───────────────────────────────────────────────────

        new("collection", "/users/{username}/collection/folders", new[] { "GET", "POST" },
            "List or create collection folders. POST creates a new folder (body: {name}).",
            Array.Empty<string>(),
            new { method = "GET", path = "/users/rodneyfool/collection/folders" }),

        new("collection", "/users/{username}/collection/folders/{folder_id}", new[] { "GET", "POST", "DELETE" },
            "Get, rename, or delete a collection folder. Folders 0 (All) and 1 (Uncategorized) cannot be renamed/deleted.",
            Array.Empty<string>()),

        new("collection", "/users/{username}/collection/folders/{folder_id}/releases", new[] { "GET" },
            "List items in a collection folder. Paginated.",
            new[] { "sort", "sort_order", "page", "per_page" },
            new { method = "GET", path = "/users/rodneyfool/collection/folders/0/releases", query = new { sort = "added", sort_order = "desc", per_page = "25" } }),

        new("collection", "/users/{username}/collection/releases/{release_id}", new[] { "GET" },
            "Get which folders contain a specific release (with instance info).",
            Array.Empty<string>()),

        new("collection", "/users/{username}/collection/folders/{folder_id}/releases/{release_id}", new[] { "POST" },
            "Add a release to a collection folder (use folder_id=1 for Uncategorized).",
            Array.Empty<string>(),
            new { method = "POST", path = "/users/rodneyfool/collection/folders/1/releases/249504" }),

        new("collection", "/users/{username}/collection/folders/{folder_id}/releases/{release_id}/instances/{instance_id}", new[] { "POST", "DELETE" },
            "Change rating / move instance to another folder (POST), or remove instance (DELETE).",
            Array.Empty<string>()),

        new("collection", "/users/{username}/collection/fields", new[] { "GET" },
            "List user-defined collection notes fields.",
            Array.Empty<string>()),

        new("collection", "/users/{username}/collection/folders/{folder_id}/releases/{release_id}/instances/{instance_id}/fields/{field_id}", new[] { "POST" },
            "Edit a notes field value on a specific collection instance.",
            Array.Empty<string>()),

        new("collection", "/users/{username}/collection/value", new[] { "GET" },
            "Get the minimum, median, and maximum value of a user's collection.",
            Array.Empty<string>(),
            new { method = "GET", path = "/users/rodneyfool/collection/value" }),

        // ── Wantlist ─────────────────────────────────────────────────────

        new("wantlist", "/users/{username}/wants", new[] { "GET" },
            "List releases in a user's wantlist. Paginated.",
            new[] { "page", "per_page" },
            new { method = "GET", path = "/users/rodneyfool/wants" }),

        new("wantlist", "/users/{username}/wants/{release_id}", new[] { "PUT", "POST", "DELETE" },
            "Add (PUT), edit (POST), or remove (DELETE) a release in the wantlist.",
            new[] { "notes", "rating" },
            new { method = "PUT", path = "/users/rodneyfool/wants/249504", body = new { notes = "Must have!", rating = 5 } }),

        // ── Lists ────────────────────────────────────────────────────────

        new("lists", "/users/{username}/lists", new[] { "GET" },
            "Get a user's lists. Paginated.",
            new[] { "page", "per_page" },
            new { method = "GET", path = "/users/rodneyfool/lists" }),

        new("lists", "/lists/{list_id}", new[] { "GET" },
            "Get items from a specific list.",
            Array.Empty<string>(),
            new { method = "GET", path = "/lists/123" }),

        // ── Marketplace ──────────────────────────────────────────────────

        new("marketplace", "/users/{username}/inventory", new[] { "GET" },
            "Get a seller's inventory (marketplace listings). Paginated.",
            new[] { "status", "sort", "sort_order", "page", "per_page" },
            new { method = "GET", path = "/users/rodneyfool/inventory", query = new { sort = "price", sort_order = "asc" } }),

        new("marketplace", "/marketplace/listings/{listing_id}", new[] { "GET", "POST", "DELETE" },
            "Get, edit, or delete a marketplace listing.",
            new[] { "curr_abbr" },
            new { method = "GET", path = "/marketplace/listings/172723812" }),

        new("marketplace", "/marketplace/listings", new[] { "POST" },
            "Create a new marketplace listing.",
            new[] { "release_id", "condition", "price", "status", "sleeve_condition", "comments", "allow_offers", "external_id", "location", "weight", "format_quantity" }),

        new("marketplace", "/marketplace/orders", new[] { "GET" },
            "List the authenticated user's orders. Paginated.",
            new[] { "status", "created_after", "created_before", "archived", "sort", "sort_order", "page", "per_page" },
            new { method = "GET", path = "/marketplace/orders", query = new { sort = "created", sort_order = "desc" } }),

        new("marketplace", "/marketplace/orders/{order_id}", new[] { "GET", "POST" },
            "Get or edit an order (status, shipping).",
            Array.Empty<string>()),

        new("marketplace", "/marketplace/orders/{order_id}/messages", new[] { "GET", "POST" },
            "List or add messages for an order. Paginated.",
            Array.Empty<string>()),

        new("marketplace", "/marketplace/fee/{price}", new[] { "GET" },
            "Calculate the Discogs marketplace fee for a given price.",
            Array.Empty<string>(),
            new { method = "GET", path = "/marketplace/fee/10.00" }),

        new("marketplace", "/marketplace/fee/{price}/{currency}", new[] { "GET" },
            "Calculate the marketplace fee for a price in a specific currency.",
            Array.Empty<string>(),
            new { method = "GET", path = "/marketplace/fee/10.00/EUR" }),

        new("marketplace", "/marketplace/price_suggestions/{release_id}", new[] { "GET" },
            "Get price suggestions for a release (requires seller settings).",
            Array.Empty<string>(),
            new { method = "GET", path = "/marketplace/price_suggestions/249504" }),

        new("marketplace", "/marketplace/stats/{release_id}", new[] { "GET" },
            "Get marketplace statistics for a release (items for sale, lowest price).",
            new[] { "curr_abbr" },
            new { method = "GET", path = "/marketplace/stats/249504" }),

        // ── Inventory Export ─────────────────────────────────────────────

        new("inventory", "/inventory/export", new[] { "GET", "POST" },
            "GET: list recent inventory exports. POST: request a new CSV export. Paginated (GET).",
            new[] { "page", "per_page" }),

        new("inventory", "/inventory/export/{id}", new[] { "GET" },
            "Get details about the status of an inventory export.",
            Array.Empty<string>()),

        new("inventory", "/inventory/export/{id}/download", new[] { "GET" },
            "Download the results of an inventory export (CSV).",
            Array.Empty<string>()),

        // ── Inventory Upload ─────────────────────────────────────────────

        new("inventory", "/inventory/upload/add", new[] { "POST" },
            "Upload a CSV to add listings to your inventory.",
            Array.Empty<string>()),

        new("inventory", "/inventory/upload/change", new[] { "POST" },
            "Upload a CSV to change existing listings in your inventory.",
            Array.Empty<string>()),

        new("inventory", "/inventory/upload/delete", new[] { "POST" },
            "Upload a CSV to delete listings from your inventory.",
            Array.Empty<string>()),

        new("inventory", "/inventory/upload", new[] { "GET" },
            "Get a list of recent inventory uploads. Paginated.",
            new[] { "page", "per_page" }),

        new("inventory", "/inventory/upload/{id}", new[] { "GET" },
            "Get details about the status of an inventory upload.",
            Array.Empty<string>()),
    };
}
