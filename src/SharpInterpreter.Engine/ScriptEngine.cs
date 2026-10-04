// SharpInterpreter — Licensed under the MIT License.

using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using SharpInterpreter.Script;

namespace SharpInterpreter.Engine;

public sealed record ScriptResult(
    bool Ok, string Output, string? Error, IReadOnlyList<ChartSpec> Charts, double CompileMs, double RunMs);

/// <summary>Compiles a C# script (top-level statements) with Roslyn and runs it, capturing what it prints.</summary>
public sealed partial class ScriptEngine
{
    private const string Usings = """
        global using System;
        global using System.Linq;
        global using System.Collections.Generic;
        global using System.Globalization;
        global using SharpInterpreter.Script;
        """;

    private readonly List<MetadataReference> _references;
    private static readonly CSharpParseOptions s_parse = new(LanguageVersion.Latest);

    public ScriptEngine() : this(EmbeddedScriptLibrary()) { }

    private static byte[] EmbeddedScriptLibrary()
    {
        using var stream = typeof(ScriptEngine).Assembly.GetManifestResourceStream("SharpInterpreter.Script.dll")!;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    /// <param name="scriptLibrary">The SharpInterpreter.Script assembly image (Roslyn needs its metadata).</param>
    public ScriptEngine(byte[] scriptLibrary)
    {
        _references = [.. Basic.Reference.Assemblies.Net100.References.All, MetadataReference.CreateFromImage(scriptLibrary)];
    }

    public ScriptResult Run(string code, IDictionary<string, byte[]> files)
    {
        var compileTimer = Stopwatch.StartNew();
        var compilation = CSharpCompilation.Create(
            "script-" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(Usings, s_parse, "usings.cs", Encoding.UTF8), CSharpSyntaxTree.ParseText(code, s_parse, "script.cs", Encoding.UTF8)],
            _references,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication, optimizationLevel: OptimizationLevel.Release, allowUnsafe: false, concurrentBuild: false));

        using var image = new MemoryStream();
        // No PDB: emitting one waits on a background task, which the single-threaded browser runtime can't do.
        var emitted = compilation.Emit(image);
        compileTimer.Stop();
        if (!emitted.Success)
        {
            var errors = emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(10)
                .Select(d => $"line {d.Location.GetLineSpan().StartLinePosition.Line + 1}: {d.Id} {d.GetMessage()}");
            return new ScriptResult(false, "", "Compile error:\n" + string.Join("\n", errors), [], compileTimer.Elapsed.TotalMilliseconds, 0);
        }

        ScriptHost.Reset(files);
        var output = new StringWriter();
        var previous = Console.Out;
        var runTimer = Stopwatch.StartNew();
        try
        {
            Console.SetOut(output);
            var entry = Assembly.Load(image.ToArray()).EntryPoint!;
            var result = entry.Invoke(null, entry.GetParameters().Length == 1 ? [Array.Empty<string>()] : []);
            if (result is Task task)
                task.GetAwaiter().GetResult();
            return new ScriptResult(true, output.ToString(), null, [.. ScriptHost.Charts], compileTimer.Elapsed.TotalMilliseconds, runTimer.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException { InnerException: { } e } ? e : ex;
            var line = ScriptLine().Match(inner.StackTrace ?? "") is { Success: true } m ? $" (line {m.Groups[1].Value})" : "";
            return new ScriptResult(false, output.ToString(), $"{inner.GetType().Name}: {inner.Message}{line}", [.. ScriptHost.Charts],
                compileTimer.Elapsed.TotalMilliseconds, runTimer.Elapsed.TotalMilliseconds);
        }
        finally
        {
            Console.SetOut(previous);
        }
    }

    [GeneratedRegex(@"script\.cs:line (\d+)")]
    private static partial Regex ScriptLine();
}
