using System.Text;
using System.Text.Json;
using AskOurData.Contracts;
using AskOurData.Web.Localization;

namespace AskOurData.Web.Components.Chat;

public static class TableFormat
{
    public static bool IsNumeric(ColumnInfo c) => c.Type is "int" or "money" or "decimal";

    public static string Format(object? value, ColumnInfo c)
    {
        if (value is null) return "";
        if (value is JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Null) return "";
            if (e.ValueKind == JsonValueKind.Number)
            {
                var d = e.GetDecimal();
                return c.Type switch
                {
                    "money" => d.ToString("N2", Ui.NumberCulture),
                    "decimal" => d.ToString("N1", Ui.NumberCulture),
                    _ => d.ToString("N0", Ui.NumberCulture),
                };
            }
            return e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.GetRawText();
        }
        return Convert.ToString(value, Ui.NumberCulture) ?? "";
    }

    public static string FormatTotal(decimal d, ColumnInfo c) =>
        c.Type == "money" ? d.ToString("N2", Ui.NumberCulture) : d.ToString("N0", Ui.NumberCulture);

    /// <summary>
    /// CSV with UTF-8 BOM (Excel opens Arabic correctly). Cells that start with = + - @ tab or CR are prefixed
    /// with an apostrophe so business text (untrusted) can never run as a spreadsheet formula.
    /// Numbers are written raw (no thousands separators).
    /// </summary>
    public static string ToCsv(ToolResult r, Ui ui)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", r.Columns.Select(c => Cell(ui.Column(c.Name)))));
        foreach (var row in r.Rows)
            sb.AppendLine(string.Join(",", r.Columns.Select(c => Cell(Raw(row.GetValueOrDefault(c.Name)), IsNumeric(c)))));
        return sb.ToString();
    }

    /// <summary>Tab-separated for pasting into a spreadsheet.</summary>
    public static string ToTsv(ToolResult r, Ui ui)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join("\t", r.Columns.Select(c => Clean(ui.Column(c.Name)))));
        foreach (var row in r.Rows)
            sb.AppendLine(string.Join("\t", r.Columns.Select(c =>
            {
                var v = Clean(Raw(row.GetValueOrDefault(c.Name)));
                return IsNumeric(c) ? v : Neutralize(v);
            })));
        return sb.ToString();
    }

    private static string Raw(object? value) => value switch
    {
        null => "",
        JsonElement { ValueKind: JsonValueKind.Null } => "",
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString() ?? "",
        JsonElement e => e.GetRawText(),
        _ => Convert.ToString(value, Ui.NumberCulture) ?? "",
    };

    private static string Cell(string s, bool numeric = false)
    {
        if (!numeric) s = Neutralize(s);
        return s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    private static string Neutralize(string s) =>
        s.Length > 0 && s[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + s : s;

    private static string Clean(string s) => s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
}
