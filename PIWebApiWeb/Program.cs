using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

var sessions = new ConcurrentDictionary<string, PiSession>();
const string CookieName = "pi_sid";

PiSession? GetSession(HttpContext ctx) =>
    ctx.Request.Cookies.TryGetValue(CookieName, out var sid) && sessions.TryGetValue(sid, out var s) ? s : null;

app.MapPost("/api/login", async (LoginRequest req, HttpContext ctx) =>
{
    var host = string.IsNullOrWhiteSpace(req.Host) ? "davm089" : req.Host.Trim();
    var domain = string.IsNullOrWhiteSpace(req.Domain) ? "darmstadt" : req.Domain.Trim();

    // Prototype only: accept self-signed/untrusted server certificates.
    var handler = new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
    };
    var client = new HttpClient(handler) { BaseAddress = new Uri($"https://{host}/piwebapi/") };
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    // PI Web API CSRF defense: required on POST/PUT/PATCH/DELETE.
    client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
        "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($@"{domain}\{req.User}:{req.Password}")));

    var session = new PiSession(client, $@"{domain}\{req.User}", host);
    try
    {
        // Verify credentials.
        await session.GetAsync("assetservers");
    }
    catch (HttpRequestException ex)
    {
        client.Dispose();
        return Results.Json(new { error = $"{(int?)ex.StatusCode} {ex.Message} {ex.InnerException?.Message}" }, statusCode: 401);
    }

    var sid = Guid.NewGuid().ToString("N");
    sessions[sid] = session;
    ctx.Response.Cookies.Append(CookieName, sid, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict });
    return Results.Ok(new { user = session.User, host = session.Host });
});

app.MapGet("/api/me", (HttpContext ctx) =>
{
    var s = GetSession(ctx);
    return s is null ? Results.Unauthorized() : Results.Ok(new { user = s.User, host = s.Host });
});

app.MapPost("/api/logout", (HttpContext ctx) =>
{
    if (ctx.Request.Cookies.TryGetValue(CookieName, out var sid) && sessions.TryRemove(sid, out var s))
        s.Client.Dispose();
    ctx.Response.Cookies.Delete(CookieName);
    return Results.Ok();
});

app.MapGet("/api/afservers", async (HttpContext ctx) =>
{
    var s = GetSession(ctx);
    if (s is null) return Results.Unauthorized();
    var doc = await s.GetAsync("assetservers?selectedFields=Items.Name;Items.WebId");
    return Results.Json(doc["Items"]!.AsArray().Select(i => new { name = (string?)i!["Name"], webId = (string?)i["WebId"] }));
});

app.MapGet("/api/afservers/{webId}/databases", async (string webId, HttpContext ctx) =>
{
    var s = GetSession(ctx);
    if (s is null) return Results.Unauthorized();
    var doc = await s.GetAsync($"assetservers/{webId}/assetdatabases?selectedFields=Items.Name;Items.WebId");
    return Results.Json(doc["Items"]!.AsArray().Select(i => new { name = (string?)i!["Name"], webId = (string?)i["WebId"] }));
});

// Tree containing only elements of the given templates and their ancestors.
app.MapGet("/api/databases/{webId}/tree", async (string webId, string? templates, HttpContext ctx) =>
{
    var s = GetSession(ctx);
    if (s is null) return Results.Unauthorized();

    var templateList = (templates ?? "Setpoint.Element.Template.Point,Setpoint.Element.Template.Measurement")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    var root = new TreeNode("");
    foreach (var t in templateList)
    {
        var doc = await s.GetAsync(
            $"assetdatabases/{webId}/elements?searchFullHierarchy=true&templateName={Uri.EscapeDataString(t)}&maxCount=1000&selectedFields=Items.Path;Items.WebId");
        foreach (var item in doc["Items"]!.AsArray())
        {
            var node = root;
            // Path format: \\server\database\Element\Child...
            foreach (var part in ((string)item!["Path"]!).Split('\\', StringSplitOptions.RemoveEmptyEntries).Skip(2))
            {
                if (!node.Children.TryGetValue(part, out var next))
                    node.Children[part] = next = new TreeNode(part);
                node = next;
            }
            node.WebId = (string?)item["WebId"];
            node.Template = t;
        }
    }
    return Results.Json(root.ToJson().children);
});

app.MapGet("/api/elements/{webId}/attributes", async (string webId, HttpContext ctx) =>
{
    var s = GetSession(ctx);
    if (s is null) return Results.Unauthorized();
    var doc = await s.GetAsync(
        $"streamsets/{webId}/value?selectedFields=Items.Name;Items.WebId;Items.Value.Timestamp;Items.Value.Value;Items.Value.UnitsAbbreviation");

    var result = doc["Items"]!.AsArray().Select(item =>
    {
        var v = item!["Value"];
        var raw = v?["Value"];
        string display = raw switch
        {
            null => "",
            JsonObject o => (string?)o["Name"] ?? o.ToJsonString(),
            _ => raw.ToString()
        };
        var ts = (string?)v?["Timestamp"];
        // 1970 timestamps are placeholders for static attributes.
        if (ts is not null && ts.StartsWith("1970-01-01")) ts = null;
        return new { name = (string?)item["Name"], webId = (string?)item["WebId"], value = display, units = (string?)v?["UnitsAbbreviation"], timestamp = ts };
    });
    return Results.Json(result);
});

// Plot data of an element's "Data" attribute. start/end use PI time syntax, e.g. "*-1h" and "*".
app.MapGet("/api/elements/{webId}/plot", async (string webId, string? start, string? end, HttpContext ctx) =>
{
    var s = GetSession(ctx);
    if (s is null) return Results.Unauthorized();

    var attr = await s.GetAsync($"elements/{webId}/attributes?nameFilter=Data&selectedFields=Items.Name;Items.WebId;Items.DefaultUnitsNameAbbreviation");
    var dataAttr = attr["Items"]!.AsArray().FirstOrDefault(i => string.Equals((string?)i!["Name"], "Data", StringComparison.OrdinalIgnoreCase));
    if (dataAttr is null) return Results.NotFound(new { error = "Element has no Data attribute." });

    var st = Uri.EscapeDataString(string.IsNullOrWhiteSpace(start) ? "*-1h" : start.Trim());
    var en = Uri.EscapeDataString(string.IsNullOrWhiteSpace(end) ? "*" : end.Trim());
    var doc = await s.GetAsync(
        $"streams/{(string?)dataAttr["WebId"]}/plot?startTime={st}&endTime={en}&intervals=800&selectedFields=Items.Timestamp;Items.Value;Items.Good");

    var points = doc["Items"]!.AsArray()
        .Where(i => i!["Value"] is JsonValue && (i["Good"]?.GetValue<bool>() ?? true))
        .Select(i => new { t = (string?)i!["Timestamp"], v = i["Value"]!.AsValue().TryGetValue<double>(out var d) ? (double?)d : null })
        .Where(p => p.v is not null);
    return Results.Json(new { units = (string?)dataAttr["DefaultUnitsNameAbbreviation"], points });
});

// Write a value (timestamp = now) to an attribute.
app.MapPost("/api/attributes/{webId}/value", async (string webId, WriteRequest req, HttpContext ctx) =>
{
    var s = GetSession(ctx);
    if (s is null) return Results.Unauthorized();
    var body = new JsonObject { ["Timestamp"] = "*", ["Value"] = req.Value };
    using var response = await s.Client.PostAsync($"streams/{webId}/value",
        new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
    if (!response.IsSuccessStatusCode)
        return Results.Json(new { error = $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}" }, statusCode: (int)response.StatusCode);
    return Results.Ok();
});

// Shows the data reference (PI tag) behind an attribute.
app.MapGet("/api/attributes/{webId}/config", async (string webId, HttpContext ctx) =>
{
    var s = GetSession(ctx);
    if (s is null) return Results.Unauthorized();
    var attr = await s.GetAsync($"attributes/{webId}?selectedFields=Name;Path;DataReferencePlugIn;ConfigString");
    var (_, pointPath, pointError) = await ResolvePointAsync(s, webId, (string?)attr["ConfigString"]);
    return Results.Json(new
    {
        attribute = (string?)attr["Path"],
        plugIn = (string?)attr["DataReferencePlugIn"],
        configString = (string?)attr["ConfigString"],
        pointPath,
        pointError
    });
});

app.Run();

// ConfigString looks like \\server?serverId\tag?pointId;UOM=x. Use the plain \\server\tag path.
async Task<(string? WebId, string? Path, string? Error)> ResolvePointAsync(PiSession s, string attributeWebId, string? configString)
{
    if (!string.IsNullOrEmpty(configString))
    {
        var cfg = configString.Split(';')[0].TrimStart('\\');
        var slash = cfg.IndexOf('\\');
        if (slash > 0)
        {
            var path = $@"\\{cfg[..slash].Split('?')[0]}\{cfg[(slash + 1)..].Split('?')[0]}";
            try
            {
                var p = await s.GetAsync($"points?path={Uri.EscapeDataString(path)}&selectedFields=WebId;Path");
                return ((string?)p["WebId"], (string?)p["Path"] ?? path, null);
            }
            catch (HttpRequestException ex) { return (null, path, $"{(int?)ex.StatusCode} for {path}"); }
        }
    }
    try
    {
        var p = await s.GetAsync($"attributes/{attributeWebId}/point?selectedFields=WebId;Path");
        return ((string?)p["WebId"], (string?)p["Path"], null);
    }
    catch (HttpRequestException ex) { return (null, null, $"{(int?)ex.StatusCode}"); }
}

record WriteRequest(double Value);

record LoginRequest(string? Host, string? Domain, string User, string Password);

class PiSession(HttpClient client, string user, string host)
{
    public HttpClient Client { get; } = client;
    public string User { get; } = user;
    public string Host { get; } = host;

    public async Task<JsonNode> GetAsync(string path)
    {
        using var response = await Client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!;
    }
}

class TreeNode(string name)
{
    public string Name { get; } = name;
    public string? WebId { get; set; }
    public string? Template { get; set; }
    public Dictionary<string, TreeNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

    public (string name, string? webId, object[] children) ToJson() =>
        (Name, WebId, Children.Values
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => (object)c.ToJsonObject()).ToArray());

    public object ToJsonObject()
    {
        var j = ToJson();
        return new { name = j.name, webId = j.webId, template = Template, children = j.children };
    }
}
