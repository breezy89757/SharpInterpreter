// SharpInterpreter — Licensed under the MIT License.

using System.Text.Json;
using Microsoft.JSInterop;
using SharpInterpreter.Engine;

namespace SharpInterpreter.Runner;

/// <summary>The entry point runner.js calls: compile and run one script, return the result as JSON.</summary>
public static class Interop
{
    private static ScriptEngine? s_engine;

    /// <param name="files">File contents (base64) by name, as Table.Read sees them.</param>
    [JSInvokable]
    public static string Run(string code, Dictionary<string, string> files)
    {
        s_engine ??= new ScriptEngine();
        var result = s_engine.Run(code, files.ToDictionary(f => f.Key, f => Convert.FromBase64String(f.Value)));
        return JsonSerializer.Serialize(result);
    }
}
