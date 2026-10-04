// SharpInterpreter — Licensed under the MIT License.
// The demo page on http://localhost:5280 and the runner on http://127.0.0.1:5281: two sites, as a real
// deployment needs (the runner on a domain of its own, not a subdomain of the page's: browsers run
// same-site frames on one thread, so a long script would freeze the page). What matters is in the
// runner's headers: its Content-Security-Policy, and who may embed it (frame-ancestors).

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.StaticFiles;

const string PageOrigin = "http://localhost:5280";
const string RunnerOrigin = "http://127.0.0.1:5281";

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(PageOrigin, RunnerOrigin);
builder.WebHost.UseStaticWebAssets(); // the demo page, also when not run as Development
var app = builder.Build();

var runnerRoot = Path.Combine(AppContext.BaseDirectory, "runner") + Path.DirectorySeparatorChar;
var runnerPolicy = Policy(File.ReadAllText(Path.Combine(runnerRoot, "index.html")), PageOrigin);
var contentTypes = new FileExtensionContentTypeProvider();

app.MapWhen(context => context.Connection.LocalPort == new Uri(RunnerOrigin).Port, runner => runner.Run(async context =>
{
    var relative = context.Request.Path.Value?.TrimStart('/') is { Length: > 0 } p ? p : "index.html";
    var path = Path.GetFullPath(Path.Combine(runnerRoot, relative));
    if (!path.StartsWith(runnerRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
    {
        context.Response.StatusCode = 404;
        return;
    }

    var headers = context.Response.Headers;
    headers.ContentType = contentTypes.TryGetContentType(path, out var type) ? type : "application/octet-stream";
    headers.XContentTypeOptions = "nosniff";
    headers["Referrer-Policy"] = "no-referrer";
    if (relative == "index.html")
    {
        headers.ContentSecurityPolicy = runnerPolicy;
        headers.CacheControl = "no-cache";
    }

    // The publish output has a brotli copy of every file: about 12 MB instead of about 40.
    headers.Vary = "Accept-Encoding";
    if (context.Request.Headers.AcceptEncoding.ToString().Contains("br") && File.Exists(path + ".br"))
    {
        headers.ContentEncoding = "br";
        path += ".br";
    }
    await context.Response.SendFileAsync(path);
}));

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/sharp-interpreter.js", () => Results.File(Path.Combine(AppContext.BaseDirectory, "sharp-interpreter.js"), "text/javascript"));
app.MapGet("/config.js", () => Results.Text($"window.RUNNER_URL = {System.Text.Json.JsonSerializer.Serialize(RunnerOrigin + "/")};", "text/javascript"));

app.Run();

// The runner's own files, its import map (allowed by hash) and WebAssembly; no connections elsewhere, no
// frames, workers, images, styles or forms; embeddable only by the page's origin.
static string Policy(string indexHtml, string embedder)
{
    // Browsers hash an inline script's text after HTML parsing, which turns CRLF into LF.
    var html = indexHtml.Replace("\r\n", "\n").Replace('\r', '\n');
    var hashes = Regex.Matches(html, @"<script(?![^>]*\bsrc=)[^>]*>([\s\S]*?)</script>", RegexOptions.IgnoreCase)
        .Select(m => $"'sha256-{Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(m.Groups[1].Value)))}'");
    return $"default-src 'none'; script-src 'self' 'wasm-unsafe-eval' {string.Join(' ', hashes)}; connect-src 'self'; " +
           $"frame-src 'none'; worker-src 'none'; img-src 'none'; style-src 'none'; form-action 'none'; base-uri 'self'; frame-ancestors {embedder}";
}
