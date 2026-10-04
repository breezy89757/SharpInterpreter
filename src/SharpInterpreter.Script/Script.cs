// SharpInterpreter — Licensed under the MIT License.

using System.Globalization;
using System.Text;

namespace SharpInterpreter.Script;

/// <summary>What a script can see and produce: the files it was given, and the charts it draws.</summary>
public static class ScriptHost
{
    public static Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public static List<ChartSpec> Charts { get; } = [];

    public static void Reset(IDictionary<string, byte[]> files)
    {
        Files.Clear();
        foreach (var (name, bytes) in files)
            Files[Key(name)] = bytes;
        Charts.Clear();
    }

    /// <summary>Paths compare with either slash and without a leading one.</summary>
    public static string Key(string name) => name.Trim().Replace('\\', '/').TrimStart('/');

    internal static string Text(string name) =>
        Files.TryGetValue(Key(name), out var bytes)
            ? Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF')
            : throw new FileNotFoundException($"No file '{name}'. Available: {string.Join(", ", Files.Keys)}");
}

/// <summary>Reading the given files as text.</summary>
public static class Files
{
    public static string ReadText(string file) => ScriptHost.Text(file);
}

/// <summary>A CSV file as rows; query it with LINQ.</summary>
public sealed class Table
{
    public IReadOnlyList<string> Columns { get; }
    public IReadOnlyList<Row> Rows { get; }

    private Table(IReadOnlyList<string> columns, IReadOnlyList<Row> rows) { Columns = columns; Rows = rows; }

    /// <summary>Reads a CSV file (first line = headers; commas, quotes as in Excel).</summary>
    public static Table Read(string file)
    {
        var lines = ParseCsv(ScriptHost.Text(file)).Where(l => l.Count > 0 && !(l.Count == 1 && l[0].Length == 0)).ToList();
        if (lines.Count == 0)
            return new Table([], []);
        var columns = lines[0].Select(c => c.Trim()).ToList();
        var index = columns.Select((c, i) => (c, i)).ToDictionary(x => x.c, x => x.i, StringComparer.OrdinalIgnoreCase);
        return new Table(columns, lines.Skip(1).Select(l => new Row(index, l)).ToList());
    }

    private static IEnumerable<List<string>> ParseCsv(string text)
    {
        var row = new List<string>(); var cell = new StringBuilder(); var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { row.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(cell.ToString()); cell.Clear(); yield return row; row = [];
            }
            else cell.Append(c);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); yield return row; }
    }
}

/// <summary>One CSV row. row["name"] is the text; Num / Date convert it.</summary>
public sealed class Row
{
    private readonly Dictionary<string, int> _index;
    private readonly List<string> _cells;

    internal Row(Dictionary<string, int> index, List<string> cells) { _index = index; _cells = cells; }

    public string this[string column] =>
        _index.TryGetValue(column, out var i)
            ? (i < _cells.Count ? _cells[i].Trim() : "")
            : throw new KeyNotFoundException($"No column '{column}'. Columns: {string.Join(", ", _index.Keys)}");

    /// <summary>The value as a number (thousands separators, %, currency symbols ignored); 0 when empty.</summary>
    public double Num(string column)
    {
        var text = this[column].Replace(",", "").Replace("$", "").Replace("%", "").Replace("NT", "").Trim();
        return text.Length == 0 ? 0 : double.Parse(text, CultureInfo.InvariantCulture);
    }

    public DateTime Date(string column) => DateTime.Parse(this[column], CultureInfo.InvariantCulture);

    public override string ToString() => string.Join(", ", _index.OrderBy(x => x.Value).Select(x => $"{x.Key}={this[x.Key]}"));
}

/// <summary>A chart for the host to draw (it gets the data, not an image).</summary>
public sealed record ChartSpec(string Type, string Title, IReadOnlyList<string> Labels, IReadOnlyList<Series> Series);

public sealed record Series(string Name, IReadOnlyList<double> Values);

public static class Chart
{
    /// <summary>Bar chart: one bar per label; pass several series for grouped bars.</summary>
    public static void Bar(string title, IEnumerable<string> labels, params Series[] series) => Add("bar", title, labels, series);

    public static void Bar(string title, IEnumerable<string> labels, IEnumerable<double> values) => Add("bar", title, labels, [new Series(title, values.ToList())]);

    /// <summary>Line chart over the labels (e.g. months).</summary>
    public static void Line(string title, IEnumerable<string> labels, params Series[] series) => Add("line", title, labels, series);

    public static void Line(string title, IEnumerable<string> labels, IEnumerable<double> values) => Add("line", title, labels, [new Series(title, values.ToList())]);

    public static void Pie(string title, IEnumerable<string> labels, IEnumerable<double> values) => Add("pie", title, labels, [new Series(title, values.ToList())]);

    private static void Add(string type, string title, IEnumerable<string> labels, Series[] series) =>
        ScriptHost.Charts.Add(new ChartSpec(type, title, labels.ToList(), series));
}

public static class Out
{
    /// <summary>Prints rows as a Markdown table (pick the columns with a projection, e.g. x => new { x.Name, x.Total }).</summary>
    public static void Table<T>(IEnumerable<T> rows)
    {
        var props = typeof(T).GetProperties();
        Console.WriteLine("| " + string.Join(" | ", props.Select(p => p.Name)) + " |");
        Console.WriteLine("|" + string.Concat(props.Select(_ => "---|")));
        foreach (var row in rows)
            Console.WriteLine("| " + string.Join(" | ", props.Select(p => Format(p.GetValue(row)))) + " |");
    }

    private static string Format(object? value) => value switch
    {
        double d => d.ToString("#,0.##", CultureInfo.InvariantCulture),
        decimal m => m.ToString("#,0.##", CultureInfo.InvariantCulture),
        float f => f.ToString("#,0.##", CultureInfo.InvariantCulture),
        DateTime t => t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        null => "",
        _ => value.ToString() ?? "",
    };
}
