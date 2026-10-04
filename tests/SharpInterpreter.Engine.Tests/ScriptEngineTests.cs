// SharpInterpreter — Licensed under the MIT License.

using System.Text;
using SharpInterpreter.Engine;

namespace SharpInterpreter.Engine.Tests;

public class ScriptEngineTests
{
    private static readonly ScriptEngine s_engine = new();

    private static ScriptResult Run(string code, params (string Name, string Content)[] files) =>
        s_engine.Run(code, files.ToDictionary(f => f.Name, f => Encoding.UTF8.GetBytes(f.Content)));

    private const string Sales = "region,month,revenue\nNorth,2026-07,\"128,000\"\nSouth,2026-07,96000\nNorth,2026-08,141000\n";

    [Fact]
    public void PrintsWhatTheScriptWrites()
    {
        var result = Run("Console.WriteLine(6 * 7);");

        Assert.True(result.Ok, result.Error);
        Assert.Equal("42", result.Output.Trim());
    }

    [Fact]
    public void ReadsCsvAndGroups()
    {
        var result = Run("""
            var t = Table.Read("data/sales.csv");
            foreach (var g in t.Rows.GroupBy(r => r["region"]).OrderBy(g => g.Key))
                Console.WriteLine($"{g.Key}={g.Sum(r => r.Num("revenue"))}");
            """, (@"data\sales.csv", Sales));

        Assert.True(result.Ok, result.Error);
        Assert.Equal(["North=269000", "South=96000"], result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()));
    }

    [Fact]
    public void ReturnsCharts()
    {
        var result = Run("""
            Chart.Bar("Revenue", new[] { "North", "South" }, new[] { 425000.0, 297000.0 });
            Chart.Line("Trend", new[] { "Jul", "Aug" }, new Series("North", [1, 2]), new Series("South", [3, 4]));
            """);

        Assert.True(result.Ok, result.Error);
        Assert.Collection(result.Charts,
            bar => { Assert.Equal("bar", bar.Type); Assert.Equal([425000.0, 297000.0], bar.Series.Single().Values); },
            line => { Assert.Equal("line", line.Type); Assert.Equal(2, line.Series.Count); });
    }

    [Fact]
    public void PrintsMarkdownTables()
    {
        var result = Run("""Out.Table(new[] { new { Name = "North", Total = 1234.5 } });""");

        Assert.True(result.Ok, result.Error);
        Assert.Contains("| North | 1,234.5 |", result.Output);
    }

    [Fact]
    public void ReportsCompileErrorsWithLines()
    {
        var result = Run("var x = 1;\nConsole.WriteLine(y);");

        Assert.False(result.Ok);
        Assert.Contains("line 2", result.Error);
        Assert.Contains("CS0103", result.Error);
    }

    [Fact]
    public void ReportsRuntimeErrorsAndKeepsOutputSoFar()
    {
        var result = Run("Console.WriteLine(\"before\");\nTable.Read(\"missing.csv\");");

        Assert.False(result.Ok);
        Assert.Contains("before", result.Output);
        Assert.Contains("FileNotFoundException", result.Error);
    }

    [Fact]
    public void NamesTheColumnsWhenOneIsMissing()
    {
        var result = Run("""Console.WriteLine(Table.Read("s.csv").Rows[0]["revenu"]);""", ("s.csv", Sales));

        Assert.False(result.Ok);
        Assert.Contains("region, month, revenue", result.Error);
    }

    [Fact]
    public void RunsAreIndependent()
    {
        Run("""Chart.Pie("A", new[] { "x" }, new[] { 1.0 });""");
        var second = Run("Console.WriteLine(1);");

        Assert.Empty(second.Charts);
    }
}
