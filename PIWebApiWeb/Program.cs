using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        var ex = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;

        if (ex is HttpRequestException httpEx)
        {
            context.Response.StatusCode = (int?)httpEx.StatusCode ?? StatusCodes.Status502BadGateway;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new { error = httpEx.Message });
            return;
        }

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new { error = ex?.Message ?? "Unexpected server error." });
    });
});

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = c =>
    {
        c.Context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        c.Context.Response.Headers.Pragma = "no-cache";
        c.Context.Response.Headers.Expires = "0";
    }
});

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

app.MapPost("/api/afservers/{webId}/databases", async (string webId, CreateDatabaseRequest req, HttpContext ctx) =>
{
    var s = GetSession(ctx);
    if (s is null) return Results.Unauthorized();

    var name = (req.Name ?? "").Trim();
    if (string.IsNullOrWhiteSpace(name))
        return Results.BadRequest(new { error = "Database name is required." });

    var existing = await s.GetAsync($"assetservers/{webId}/assetdatabases?selectedFields=Items.Name");
    var exists = existing["Items"]!.AsArray().Any(i => string.Equals((string?)i!["Name"], name, StringComparison.OrdinalIgnoreCase));
    if (exists)
        return Results.Json(new { error = $"Database '{name}' already exists." }, statusCode: 409);

    var body = new JsonObject { ["Name"] = name };
    using var response = await s.Client.PostAsync($"assetservers/{webId}/assetdatabases",
        new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));

    if (!response.IsSuccessStatusCode)
        return Results.Json(new { error = $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}" }, statusCode: (int)response.StatusCode);

    return Results.Ok(new { name });
});

async Task<IResult> ImportAfTemplateAsync(string webId, HttpRequest request, HttpContext ctx)
{
    var s = GetSession(ctx);
    if (s is null) return Results.Unauthorized();

    if (!request.HasFormContentType)
        return Results.BadRequest(new { error = "Expected multipart/form-data." });

    var form = await request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file is null || file.Length == 0)
        return Results.BadRequest(new { error = "AF template XML file is required." });

    // Support both encoded and catch-all route values.
    webId = Uri.UnescapeDataString(webId).Trim('/');

    // PI Web API 2023 SP1+ no longer offers POST assetdatabases/{webId}/import, so we import with
    // AFImport.exe (ships with PI AF Client). We still use PI Web API to resolve the DB's AF path.
    string? dbPath;
    try
    {
        dbPath = (string?)(await s.GetAsync($"assetdatabases/{webId}?selectedFields=Path"))["Path"];
    }
    catch (HttpRequestException ex) when ((int?)ex.StatusCode == 404)
    {
        return Results.Json(new
        {
            error = "Database WebId was not found on PI Web API server.",
            webId,
            hint = "Refresh database list and select the database again."
        }, statusCode: 404);
    }
    if (string.IsNullOrWhiteSpace(dbPath))
        return Results.Json(new { error = "PI Web API did not return a path for this database." }, statusCode: 502);

    var exePath = FindAfImportExe();
    if (exePath is null)
        return Results.Json(new
        {
            error = "AFImport.exe was not found. Install PI AF Client on the machine running this app, or set AfImport:ExePath."
        }, statusCode: StatusCodes.Status501NotImplemented);

    var tempFile = Path.Combine(Path.GetTempPath(), $"afimport_{Guid.NewGuid():N}.xml");
    try
    {
        await using (var fs = File.Create(tempFile))
            await file.CopyToAsync(fs);

        var psi = new ProcessStartInfo(exePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(dbPath);              // e.g. \\KYBELE\APITEST1
        psi.ArgumentList.Add($"/File:{tempFile}");
        foreach (var a in (app.Configuration["AfImport:ExtraArgs"] ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
            psi.ArgumentList.Add(a);               // e.g. "/P" to create/update PI points

        var timeout = TimeSpan.FromSeconds(app.Configuration.GetValue("AfImport:TimeoutSeconds", 300));
        using var proc = Process.Start(psi)!;
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(timeout);
        try { await proc.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            return Results.Json(new { error = $"AFImport timed out after {timeout.TotalSeconds:0} s." }, statusCode: 504);
        }

        var output = ((await stdoutTask) + "\n" + (await stderrTask)).Trim();
        app.Logger.LogInformation("AFImport {Db} exit {Code}: {Output}", dbPath, proc.ExitCode, output);

        if (proc.ExitCode != 0)
            return Results.Json(new
            {
                error = $"AFImport failed (exit {proc.ExitCode}) for {dbPath}: {(output.Length > 0 ? output : "(no output)")}",
                exitCode = proc.ExitCode,
            }, statusCode: StatusCodes.Status502BadGateway);

        return Results.Ok(new { message = "AF template imported successfully.", database = dbPath, importedFile = file.FileName, details = output });
    }
    finally
    {
        try { File.Delete(tempFile); } catch { }
    }
}

string? FindAfImportExe()
{
    var configured = app.Configuration["AfImport:ExePath"];
    if (!string.IsNullOrWhiteSpace(configured))
        return File.Exists(configured) ? configured : null;

    var candidates = new[]
    {
        Environment.GetEnvironmentVariable("PIHOME64"),
        Environment.GetEnvironmentVariable("PIHOME"),
        @"C:\Program Files\PIPC",
    };
    return candidates
        .Where(d => !string.IsNullOrWhiteSpace(d))
        .Select(d => Path.Combine(d!, "AF", "AFImport.exe"))
        .FirstOrDefault(File.Exists);
}

app.MapPost("/api/databases/{webId}/import", ImportAfTemplateAsync);

// Helpful response if client accidentally uses GET.
app.MapGet("/api/databases/{webId}/import", (string webId) =>
    Results.Json(new
    {
        error = "Use POST multipart/form-data to import AF template XML.",
        webId
    }, statusCode: StatusCodes.Status405MethodNotAllowed));

// Fallback for clients that send slash-containing IDs that bypass normal segment matching.
app.MapPost("/api/databases/{*route}", async (string route, HttpRequest request, HttpContext ctx) =>
{
    const string suffix = "/import";
    if (!route.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        return Results.NotFound();

    var webId = route[..^suffix.Length];
    if (string.IsNullOrWhiteSpace(webId))
        return Results.BadRequest(new { error = "Database WebId is required." });

    return await ImportAfTemplateAsync(webId, request, ctx);
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
        var selectedFields = Uri.EscapeDataString("Items.Path;Items.WebId");
        JsonNode? doc;
        try
        {
            doc = await s.GetAsync(
                $"assetdatabases/{webId}/elements?searchFullHierarchy=true&templateName={Uri.EscapeDataString(t)}&maxCount=1000&selectedFields={selectedFields}");
        }
        catch (HttpRequestException ex)
            when ((int?)ex.StatusCode == 400 && ex.Message.Contains("element template was not found", StringComparison.OrdinalIgnoreCase))
        {
            // New/empty DB: template does not exist yet. Skip and continue.
            continue;
        }
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
record CreateDatabaseRequest(string? Name);

record LoginRequest(string? Host, string? Domain, string User, string Password);

class PiSession(HttpClient client, string user, string host)
{
    public HttpClient Client { get; } = client;
    public string User { get; } = user;
    public string Host { get; } = host;

    public async Task<JsonNode> GetAsync(string path)
    {
        using var response = await Client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"GET {path} failed: {(int)response.StatusCode} {response.ReasonPhrase}. Body: {body}",
                null,
                response.StatusCode);

        return JsonNode.Parse(body)!;
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
