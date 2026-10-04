// SharpInterpreter — Licensed under the MIT License.

using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using SharpInterpreter.Engine;

namespace SharpInterpreter.Runner;

/// <summary>The entry point worker.js calls: compile and run one script, return the result as JSON.</summary>
[SupportedOSPlatform("browser")]
public static partial class Interop
{
    private static ScriptEngine? s_engine;

    /// <param name="filesJson">File contents (base64) by name, as Table.Read sees them: <c>{ "sales.csv": "…" }</c>.</param>
    [JSExport]
    public static string Run(string code, string filesJson)
    {
        s_engine ??= new ScriptEngine();
        var files = JsonSerializer.Deserialize<Dictionary<string, string>>(filesJson) ?? [];
        var result = s_engine.Run(code, files.ToDictionary(f => f.Key, f => Convert.FromBase64String(f.Value)));
        return JsonSerializer.Serialize(result);
    }
}
