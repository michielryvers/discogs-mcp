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
            Execute a single API call. The server handles auth and rate limiting. JSON is compact by default.
            ```json
            { "method": "GET", "path": "/database/search", "query": { "q": "Nirvana Nevermind", "type": "release", "per_page": "5" } }
            { "method": "GET", "path": "/releases/249504" }
            { "method": "GET", "path": "/users/{your_username}/collection/folders" }
            { "method": "PUT", "path": "/users/{your_username}/wants/249504", "body": { "notes": "Classic!", "rating": 5 } }
            { "method": "GET", "path": "/database/search", "query": { "barcode": "042282449917", "type": "release" }, "extract": "results", "view": "light" }
            { "method": "GET", "path": "/database/search", "query": { "q": "Blue Note", "type": "release" }, "extract": "results", "fields": ["id", "title", "year"] }
            { "method": "GET", "path": "/releases/249504", "view": "light" }
            { "method": "GET", "path": "/releases/249504", "pretty": true }
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
            {
              "method": "GET",
              "path": "/database/search",
              "query": { "barcode": "042282449917", "type": "release" },
              "page": 1, "perPage": 10, "maxPages": 2,
              "extract": "results",
              "view": "light"
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
            - `view` defaults to `auto` (lists → light summaries; single objects → full).
            - `fields` applies to list items when a list is present.
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
     Description("Call any Discogs REST API endpoint. The server handles authentication, User-Agent, and rate-limit headers. Returns status, headers, and parsed JSON (or raw text) with compact JSON by default.")]
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
        [Description("Extract a JSON property from the response (optional)")]
        string? extract = null,
        [Description("Select a subset of fields from list items or arrays (optional)")]
        string[]? fields = null,
        [Description("Response view: full, light, or auto (default auto)")]
        string? view = null,
        [Description("Accept header override (optional, defaults to application/vnd.discogs.v2+json)")]
        string? accept = null,
        [Description("Pretty-print JSON output (default false)")]
        bool pretty = false)
    {
        var client = httpClientFactory.CreateClient("discogs");
        var result = await ExecuteDiscogsRequest(client, method, path, query, body, accept);

        if (result.Json.HasValue)
        {
            var json = result.Json.Value;

            if (!string.IsNullOrWhiteSpace(extract) && TryExtractProperty(json, extract, out var extracted))
                json = extracted;

            var viewMode = ResolveView(NormalizeView(view), IsListResponse(json));
            json = ApplyView(json, viewMode);

            if (fields is { Length: > 0 })
            {
                var fieldSet = new HashSet<string>(fields, StringComparer.OrdinalIgnoreCase);
                json = ApplyFieldsToListResponse(json, fieldSet);
            }

            result.Json = json;
        }

        return JsonSerializer.Serialize(result, JsonOpts.ForOutput(pretty));
    }

    // ── discogs_paginate ─────────────────────────────────────────────────

    [McpServerTool(Name = "discogs_paginate"),
     Description("Fetch multiple pages from a paginated Discogs endpoint and return concatenated items. Specify the 'extract' key to pull items from each page's response (e.g. 'results', 'releases', 'wants', 'listings'). Compact JSON by default with optional view/fields shaping.")]
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
        string? extract = null,
        [Description("Response view: full, light, or auto (default auto)")]
        string? view = null,
        [Description("Select a subset of fields from list items or arrays (optional)")]
        string[]? fields = null,
        [Description("Pretty-print JSON output (default false)")]
        bool pretty = false)
    {
        maxPages = Math.Clamp(maxPages, 1, 10);
        perPage = Math.Clamp(perPage, 1, 100);

        var client = httpClientFactory.CreateClient("discogs");
        var allItems = new List<JsonElement>();
        int pagesFetched = 0;
        int? lastPage = null;
        int? nextPage = null;
        var viewMode = ResolveView(NormalizeView(view), true);
        var fieldSet = fields is { Length: > 0 }
            ? new HashSet<string>(fields, StringComparer.OrdinalIgnoreCase)
            : null;

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
                        allItems.Add(ProcessItem(item, viewMode, fieldSet));
                }
                else if (json.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in json.EnumerateArray())
                        allItems.Add(ProcessItem(item, viewMode, fieldSet));
                }
                else
                {
                    // Can't extract, just add the whole response
                    var processed = ApplyView(json, viewMode);
                    if (fieldSet != null)
                        processed = ApplyFields(processed, fieldSet);
                    allItems.Add(processed);
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

        return JsonSerializer.Serialize(output, JsonOpts.ForOutput(pretty));
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

    // ── Response shaping ───────────────────────────────────────────────

    private static string NormalizeView(string? view)
    {
        if (string.IsNullOrWhiteSpace(view))
            return "auto";

        return view.Trim().ToLowerInvariant() switch
        {
            "full" => "full",
            "light" => "light",
            "auto" => "auto",
            _ => "auto"
        };
    }

    private static string ResolveView(string view, bool isList)
        => view == "auto" ? (isList ? "light" : "full") : view;

    private static bool TryExtractProperty(JsonElement json, string extract, out JsonElement extracted)
    {
        extracted = json;
        if (json.ValueKind != JsonValueKind.Object)
            return false;

        var current = json;
        var segments = extract.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
            return false;

        foreach (var segment in segments)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out var next))
                return false;
            current = next;
        }

        extracted = current;
        return true;
    }

    private static bool IsListResponse(JsonElement json)
    {
        if (json.ValueKind == JsonValueKind.Array)
            return true;

        if (json.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var key in ListKeys)
        {
            if (json.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Array)
                return true;
        }

        return false;
    }

    private static JsonElement ApplyView(JsonElement json, string view)
    {
        if (view != "light")
            return json;

        if (json.ValueKind == JsonValueKind.Array)
        {
            var items = new List<object?>();
            foreach (var item in json.EnumerateArray())
                items.Add(ToLightItem(item));
            return JsonSerializer.SerializeToElement(items, JsonOpts.Compact);
        }

        if (json.ValueKind == JsonValueKind.Object)
        {
            var hasList = false;
            var obj = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            foreach (var prop in json.EnumerateObject())
            {
                if (IsListKey(prop.Name) && prop.Value.ValueKind == JsonValueKind.Array)
                {
                    var items = new List<object?>();
                    foreach (var item in prop.Value.EnumerateArray())
                        items.Add(ToLightItem(item));
                    obj[prop.Name] = items;
                    hasList = true;
                }
                else
                {
                    obj[prop.Name] = prop.Value.Clone();
                }
            }

            if (hasList)
                return JsonSerializer.SerializeToElement(obj, JsonOpts.Compact);

            return ToLightItem(json);
        }

        return json;
    }

    private static JsonElement ApplyFields(JsonElement json, IEnumerable<string> fields)
    {
        var fieldSet = fields is HashSet<string> set
            ? set
            : new HashSet<string>(fields, StringComparer.OrdinalIgnoreCase);

        if (json.ValueKind == JsonValueKind.Array)
        {
            var items = new List<object?>();
            foreach (var item in json.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                    items.Add(ApplyFields(item, fieldSet));
                else
                    items.Add(item.Clone());
            }
            return JsonSerializer.SerializeToElement(items, JsonOpts.Compact);
        }

        if (json.ValueKind != JsonValueKind.Object)
            return json;

        var obj = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in json.EnumerateObject())
        {
            if (fieldSet.Contains(prop.Name))
                obj[prop.Name] = prop.Value.Clone();
        }

        return JsonSerializer.SerializeToElement(obj, JsonOpts.Compact);
    }

    private static JsonElement ApplyFieldsToListResponse(JsonElement json, HashSet<string> fields)
    {
        if (json.ValueKind == JsonValueKind.Array)
            return ApplyFields(json, fields);

        if (json.ValueKind != JsonValueKind.Object)
            return json;

        var hasList = false;
        var obj = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var prop in json.EnumerateObject())
        {
            if (IsListKey(prop.Name) && prop.Value.ValueKind == JsonValueKind.Array)
            {
                var items = new List<object?>();
                foreach (var item in prop.Value.EnumerateArray())
                    items.Add(ApplyFields(item, fields));
                obj[prop.Name] = items;
                hasList = true;
            }
            else
            {
                obj[prop.Name] = prop.Value.Clone();
            }
        }

        return hasList
            ? JsonSerializer.SerializeToElement(obj, JsonOpts.Compact)
            : ApplyFields(json, fields);
    }

    private static JsonElement ProcessItem(JsonElement item, string viewMode, HashSet<string>? fields)
    {
        var processed = ApplyView(item, viewMode);
        if (fields != null)
            processed = ApplyFields(processed, fields);
        return processed;
    }

    private static JsonElement ToLightItem(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return item.Clone();

        var source = item;
        if (item.TryGetProperty("basic_information", out var basicInfo) && basicInfo.ValueKind == JsonValueKind.Object)
            source = basicInfo;
        else if (item.TryGetProperty("release", out var release) && release.ValueKind == JsonValueKind.Object)
            source = release;
        else if (item.TryGetProperty("master", out var master) && master.ValueKind == JsonValueKind.Object)
            source = master;

        var id = GetStringOrNumber(item, "id") ?? GetStringOrNumber(source, "id");
        var title = GetString(source, "title");
        var artist = GetString(source, "artist") ?? GetArtistFromArray(source, "artists") ?? GetString(source, "artists_sort");

        if (string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(title))
        {
            var parts = title.Split(" - ", 2, StringSplitOptions.None);
            if (parts.Length == 2)
            {
                artist = parts[0].Trim();
                title = parts[1].Trim();
            }
        }

        var label = GetFirstString(source, "label") ?? GetNameFromArray(source, "labels");
        var catalogNumber = GetString(source, "catno") ?? GetString(source, "catalog_number") ?? GetCatnoFromLabels(source);
        var barcode = GetFirstString(source, "barcode") ?? GetBarcodeFromIdentifiers(source);
        var year = GetStringOrNumber(source, "year");
        var country = GetString(source, "country");
        var format = GetFormat(source);

        var hasReleaseFields =
            !string.IsNullOrWhiteSpace(title) ||
            !string.IsNullOrWhiteSpace(artist) ||
            !string.IsNullOrWhiteSpace(label) ||
            !string.IsNullOrWhiteSpace(catalogNumber) ||
            !string.IsNullOrWhiteSpace(barcode) ||
            !string.IsNullOrWhiteSpace(year) ||
            !string.IsNullOrWhiteSpace(country) ||
            !string.IsNullOrWhiteSpace(format);

        if (hasReleaseFields)
        {
            var output = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = id,
                ["title"] = title,
                ["artist"] = artist,
                ["label"] = label,
                ["catalogNumber"] = catalogNumber,
                ["barcode"] = barcode,
                ["year"] = year,
                ["country"] = country,
                ["format"] = format
            };

            return JsonSerializer.SerializeToElement(output, JsonOpts.Compact);
        }

        return ToGenericLightItem(item, id);
    }

    private static JsonElement ToGenericLightItem(JsonElement item, string? id)
    {
        var title = GetString(item, "title")
            ?? GetString(item, "name")
            ?? GetString(item, "subject");

        var type = GetString(item, "type") ?? GetString(item, "status");
        var uri = GetString(item, "uri") ?? GetString(item, "resource_url");

        var output = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = id,
            ["title"] = title,
            ["type"] = type,
            ["uri"] = uri
        };

        return JsonSerializer.SerializeToElement(output, JsonOpts.Compact);
    }

    private static string? GetString(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var prop))
            return null;

        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString(),
            JsonValueKind.Number => prop.GetRawText(),
            _ => null
        };
    }

    private static string? GetStringOrNumber(JsonElement obj, string propertyName)
        => GetString(obj, propertyName);

    private static string? GetFirstString(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var prop))
            return null;
        return GetFirstStringFromElement(prop);
    }

    private static string? GetFirstStringFromElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.Array => GetFirstStringFromArray(element),
            JsonValueKind.Object => GetString(element, "name") ?? GetString(element, "title"),
            _ => null
        };
    }

    private static string? GetFirstStringFromArray(JsonElement array)
    {
        foreach (var item in array.EnumerateArray())
        {
            var value = GetFirstStringFromElement(item);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static string? GetNameFromArray(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var prop) || prop.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var item in prop.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                return name.GetString();
        }

        return null;
    }

    private static string? GetArtistFromArray(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var prop) || prop.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var item in prop.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                return name.GetString();
        }

        return null;
    }

    private static string? GetCatnoFromLabels(JsonElement obj)
    {
        if (!obj.TryGetProperty("labels", out var labels) || labels.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var label in labels.EnumerateArray())
        {
            if (label.ValueKind == JsonValueKind.Object && label.TryGetProperty("catno", out var catno))
                return GetFirstStringFromElement(catno);
        }

        return null;
    }

    private static string? GetBarcodeFromIdentifiers(JsonElement obj)
    {
        if (!obj.TryGetProperty("identifiers", out var identifiers) || identifiers.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var ident in identifiers.EnumerateArray())
        {
            if (ident.ValueKind != JsonValueKind.Object)
                continue;

            if (ident.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String)
            {
                var type = typeElement.GetString() ?? string.Empty;
                if (!type.Contains("Barcode", StringComparison.OrdinalIgnoreCase))
                    continue;
            }
            else
            {
                continue;
            }

            if (ident.TryGetProperty("value", out var value))
                return GetFirstStringFromElement(value);
        }

        return null;
    }

    private static string? GetFormat(JsonElement obj)
    {
        if (obj.TryGetProperty("format", out var format))
        {
            var parts = GetStringList(format, 4);
            if (parts.Count > 0)
                return string.Join(", ", parts);
        }

        if (obj.TryGetProperty("formats", out var formats) && formats.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in formats.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                var parts = new List<string>();
                var name = GetString(item, "name");
                if (!string.IsNullOrWhiteSpace(name))
                    parts.Add(name);

                if (item.TryGetProperty("descriptions", out var descriptions))
                    parts.AddRange(GetStringList(descriptions, 3));

                if (parts.Count > 0)
                    return string.Join(", ", parts);
            }
        }

        return null;
    }

    private static List<string> GetStringList(JsonElement element, int maxItems)
    {
        var list = new List<string>();

        if (element.ValueKind == JsonValueKind.String)
        {
            list.Add(element.GetString() ?? string.Empty);
            return list;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var value = GetFirstStringFromElement(item);
                if (!string.IsNullOrWhiteSpace(value))
                    list.Add(value);

                if (list.Count >= maxItems)
                    break;
            }
        }

        return list;
    }

    private static bool IsListKey(string name)
        => ListKeys.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static readonly string[] ListKeys = new[]
    {
        "results",
        "releases",
        "versions",
        "wants",
        "listings",
        "orders",
        "messages",
        "items",
        "lists",
        "folders",
        "submissions",
        "contributions",
        "list",
        "data"
    };
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

    public static readonly JsonSerializerOptions Compact = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static JsonSerializerOptions ForOutput(bool pretty)
        => pretty ? Indented : Compact;
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
