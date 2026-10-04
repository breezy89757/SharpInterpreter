// SharpInterpreter — Licensed under the MIT License.
// The demo page on http://localhost:5280 and the runner on http://127.0.0.1:5281: two sites, as a real
// deployment should be (the runner on a domain of its own). What matters is in the runner's headers: its
// Content-Security-Policy, on every file (the Web Worker gets its policy from its own response), and who
// may embed it (frame-ancestors).

using Microsoft.AspNetCore.StaticFiles;

const string PageOrigin = "http://localhost:5280";
const string RunnerOrigin = "http://127.0.0.1:5281";

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(PageOrigin, RunnerOrigin);
builder.WebHost.UseStaticWebAssets(); // the demo page, also when not run as Development
var app = builder.Build();

var runnerRoot = Path.Combine(AppContext.BaseDirectory, "runner") + Path.DirectorySeparatorChar;
// Its own files and WebAssembly; connections only to itself; no frames, images, styles or forms; embeddable
// only by the page's origin.
var runnerPolicy = "default-src 'none'; script-src 'self' 'wasm-unsafe-eval'; connect-src 'self'; worker-src 'self'; " +
                   $"frame-src 'none'; img-src 'none'; style-src 'none'; form-action 'none'; base-uri 'none'; frame-ancestors {PageOrigin}";
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
    headers.ContentSecurityPolicy = runnerPolicy;
    headers.CacheControl = "no-cache"; // file names aren't fingerprinted, so always check for a newer copy

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
