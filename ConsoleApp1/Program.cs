using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

Console.Write("PI Web API server name [davm089]: ");
var host = Console.ReadLine()?.Trim();
if (string.IsNullOrEmpty(host)) host = "davm089";
var BaseUrl = $"https://{host}/piwebapi/";

Console.Write("Domain [darmstadt]: ");
var domain = Console.ReadLine()?.Trim();
if (string.IsNullOrEmpty(domain)) domain = "darmstadt";

string? user = null;
while (string.IsNullOrEmpty(user))
{
    Console.Write("Username: ");
    user = Console.ReadLine()?.Trim();
}
user = $@"{domain}\{user}";

HttpClientHandler handler = new()
{
    // Prototype only: accept self-signed/untrusted server certificates.
    ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
};
HttpClient client;

Console.Write("Password: ");
var password = ReadPassword();
client = new HttpClient(handler);
client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
    "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));

client.BaseAddress = new Uri(BaseUrl);
Console.WriteLine($"Connecting to {BaseUrl} with Basic auth as: {user}");
client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

try
{
    using var servers = await GetJsonAsync("assetservers");
    var afServers = servers.RootElement.GetProperty("Items").EnumerateArray()
        .Select(s => (Name: s.GetProperty("Name").GetString()!, WebId: s.GetProperty("WebId").GetString()!))
        .ToList();
    if (afServers.Count == 0)
    {
        Console.WriteLine("No AF servers found.");
        return;
    }

    var afServer = afServers[0];
    if (afServers.Count > 1)
    {
        Console.WriteLine("AF servers:");
        for (var i = 0; i < afServers.Count; i++)
            Console.WriteLine($"  {i + 1}. {afServers[i].Name}");
        Console.Write("Choose an AF server number: ");
        if (!int.TryParse(Console.ReadLine(), out var serverChoice) || serverChoice < 1 || serverChoice > afServers.Count)
        {
            Console.WriteLine("Invalid choice.");
            return;
        }
        afServer = afServers[serverChoice - 1];
    }

    Console.WriteLine($"AF server: {afServer.Name}");
    var databases = new List<(string Name, string WebId)>();
    using (var dbs = await GetJsonAsync($"assetservers/{afServer.WebId}/assetdatabases"))
    {
        foreach (var db in dbs.RootElement.GetProperty("Items").EnumerateArray())
            databases.Add((db.GetProperty("Name").GetString()!, db.GetProperty("WebId").GetString()!));
    }

    Console.WriteLine("Databases:");
    for (var i = 0; i < databases.Count; i++)
        Console.WriteLine($"  {i + 1}. {databases[i].Name}");

    Console.Write("Choose a database number: ");
    if (int.TryParse(Console.ReadLine(), out var choice) && choice >= 1 && choice <= databases.Count)
    {
        var selected = databases[choice - 1];
        Console.Write("Element templates, comma-separated [Setpoint.Element.Template.Point,Setpoint.Element.Template.Measurement], * = browse all levels: ");
        var template = Console.ReadLine()?.Trim();
        if (template == "*")
            await BrowseAsync(selected.Name, $"assetdatabases/{selected.WebId}/elements?searchFullHierarchy=false");
        else
            await ListByTemplateAsync(selected.Name, selected.WebId, string.IsNullOrEmpty(template) ? "Setpoint.Element.Template.Point,Setpoint.Element.Template.Measurement" : template);
    }
    else
    {
        Console.WriteLine("Invalid choice.");
    }
}
catch (HttpRequestException ex)
{
    Console.WriteLine($"Request failed: {ex.StatusCode} {ex.Message} {ex.InnerException?.Message}");
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex}");
}

Console.WriteLine("Done. Press any key...");
Console.ReadKey();

async Task ListByTemplateAsync(string databaseName, string databaseWebId, string templateName)
{
    var found = new List<(string Path, string WebId)>();
    foreach (var t in templateName.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        using var doc = await GetJsonAsync(
            $"assetdatabases/{databaseWebId}/elements?searchFullHierarchy=true&templateName={Uri.EscapeDataString(t)}&maxCount=1000&selectedFields=Items.Name;Items.Path;Items.WebId");
        found.AddRange(doc.RootElement.GetProperty("Items").EnumerateArray()
            .Select(e => (Path: e.GetProperty("Path").GetString()!, WebId: e.GetProperty("WebId").GetString()!)));
    }
    found = found.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToList();

    if (found.Count == 0)
    {
        Console.WriteLine($"No elements found with template '{templateName}'.");
        return;
    }

    // Build a tree containing only the matching elements and their ancestors.
    var root = new TreeNode("");
    foreach (var (path, webId) in found)
    {
        var node = root;
        foreach (var part in path.Split('\\', StringSplitOptions.RemoveEmptyEntries).Skip(2))
        {
            if (!node.Children.TryGetValue(part, out var next))
                node.Children[part] = next = new TreeNode(part);
            node = next;
        }
        node.WebId = webId;
    }

    var stack = new Stack<TreeNode>();
    stack.Push(root);
    while (stack.Count > 0)
    {
        var current = stack.Peek();
        var children = current.Children.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();

        Console.WriteLine();
        Console.WriteLine($"== {string.Join(" > ", stack.Reverse().Skip(1).Select(s => s.Name).Prepend(databaseName))} ==");
        for (var i = 0; i < children.Count; i++)
        {
            var c = children[i];
            var marker = c.WebId is not null ? "  <match>" : "  [+]";
            Console.WriteLine($"  {i + 1,3}. {c.Name}{marker}");
        }

        Console.Write("Number = open, v = view attributes of current, u = up, q = quit: ");
        var input = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (input == "q") return;
        if (input == "u") { stack.Pop(); continue; }
        if (input == "v")
        {
            if (current.WebId is null) Console.WriteLine("Current level is not a matching element.");
            else await ShowAttributesAsync(current.WebId);
            continue;
        }
        if (int.TryParse(input, out var n) && n >= 1 && n <= children.Count)
        {
            var child = children[n - 1];
            if (child.WebId is not null) await ShowAttributesAsync(child.WebId);
            if (child.Children.Count > 0 || child.WebId is not null) stack.Push(child);
        }
        else Console.WriteLine("Invalid choice.");
    }
}

async Task BrowseAsync(string rootName, string rootChildrenPath)
{
    // Stack of (name, path used to load its children); the top is the current level.
    var stack = new Stack<(string Name, string ChildrenPath, string? WebId)>();
    stack.Push((rootName, rootChildrenPath, null));

    while (stack.Count > 0)
    {
        var current = stack.Peek();
        var sep = current.ChildrenPath.Contains('?') ? "&" : "?";
        using var doc = await GetJsonAsync(
            $"{current.ChildrenPath}{sep}maxCount=1000&selectedFields=Items.Name;Items.WebId;Items.HasChildren");
        var children = doc.RootElement.GetProperty("Items").EnumerateArray()
            .Select(e => (Name: e.GetProperty("Name").GetString()!,
                          WebId: e.GetProperty("WebId").GetString()!,
                          HasChildren: e.TryGetProperty("HasChildren", out var h) && h.GetBoolean()))
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Console.WriteLine();
        Console.WriteLine($"== {string.Join(" > ", stack.Reverse().Select(s => s.Name))} ==");
        if (children.Count == 0) Console.WriteLine("  (no child elements)");
        for (var i = 0; i < children.Count; i++)
            Console.WriteLine($"  {i + 1,3}. {children[i].Name}{(children[i].HasChildren ? "  [+]" : "")}");

        Console.Write("Number = open element, v = view attributes of current, u = up, q = quit: ");
        var input = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (input == "q") return;
        if (input == "u") { stack.Pop(); continue; }
        if (input == "v")
        {
            if (current.WebId is null) Console.WriteLine("Select an element first.");
            else await ShowAttributesAsync(current.WebId);
            continue;
        }
        if (int.TryParse(input, out var n) && n >= 1 && n <= children.Count)
        {
            var child = children[n - 1];
            await ShowAttributesAsync(child.WebId);
            stack.Push((child.Name, $"elements/{child.WebId}/elements", child.WebId));
        }
        else Console.WriteLine("Invalid choice.");
    }
}

async Task ShowAttributesAsync(string elementWebId)
{
    using var values = await GetJsonAsync(
        $"streamsets/{elementWebId}/value?selectedFields=Items.Name;Items.Value.Timestamp;Items.Value.Value;Items.Value.UnitsAbbreviation");
    Console.WriteLine("  Attributes:");
    foreach (var item in values.RootElement.GetProperty("Items").EnumerateArray())
    {
        var name = item.GetProperty("Name").GetString();
        if (item.TryGetProperty("Value", out var v) && v.ValueKind == JsonValueKind.Object)
        {
            var val = v.TryGetProperty("Value", out var vv) ? vv.ToString() : "";
            var units = v.TryGetProperty("UnitsAbbreviation", out var u) ? u.GetString() : "";
            var ts = v.TryGetProperty("Timestamp", out var t) ? t.GetString() : "";
            Console.WriteLine($"    {name}: {val} {units}  ({ts})");
        }
        else
        {
            Console.WriteLine($"    {name}: (no value)");
        }
    }
}

async Task<JsonDocument> GetJsonAsync(string path)
{
    using var response = await client.GetAsync(path);
    if (response.StatusCode == HttpStatusCode.Unauthorized)
        Console.WriteLine($"401. Server accepts: {string.Join(", ", response.Headers.WwwAuthenticate)}");
    response.EnsureSuccessStatusCode();
    return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
}

static string ReadPassword()
{
    var sb = new StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); break; }
        if (key.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; }
        else sb.Append(key.KeyChar);
    }
    return sb.ToString();
}

class TreeNode(string name)
{
    public string Name { get; } = name;
    public string? WebId { get; set; }
    public Dictionary<string, TreeNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
}
