using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AskOurData.Web.Agent;

/// <summary>
/// Checks that every number in the model's explanation comes from a tool result (or the user's question).
/// Rounding to fewer decimals and K/M abbreviations are accepted; new numbers, sums and percentages that
/// are not in the data are not.
/// </summary>
public static partial class Grounding
{
    /// <summary>Small integers are allowed freely (list numbering, "top 5", "Q3").</summary>
    public const int FreeIntegerMax = 10;

    public sealed record Claim(string Text, decimal Value, int Decimals, decimal Multiplier, bool Percent);

    public static string NormalizeDigits(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            sb.Append(ch switch
            {
                >= '٠' and <= '٩' => (char)('0' + (ch - '٠')),   // Arabic-Indic
                >= '۰' and <= '۹' => (char)('0' + (ch - '۰')),   // Extended Arabic-Indic
                '٫' => '.',   // Arabic decimal separator
                '٬' => ',',   // Arabic thousands separator
                _ => ch,
            });
        }
        return sb.ToString();
    }

    public static IReadOnlyList<Claim> ExtractClaims(string text)
    {
        var claims = new List<Claim>();
        foreach (Match m in NumberRegex().Matches(NormalizeDigits(text)))
        {
            var raw = m.Groups["num"].Value.Replace(",", "");
            if (!decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)) continue;
            var decimals = raw.Contains('.') ? raw.Length - raw.IndexOf('.') - 1 : 0;
            var suffix = m.Groups["suffix"].Value.Trim().ToLowerInvariant();
            var multiplier = suffix switch
            {
                "k" or "thousand" or "ألف" or "الف" => 1_000m,
                "m" or "mn" or "million" or "مليون" => 1_000_000m,
                "b" or "bn" or "billion" or "مليار" => 1_000_000_000m,
                _ => 1m,
            };
            claims.Add(new Claim(m.Value, value, decimals, multiplier, m.Groups["pct"].Success));
        }
        return claims;
    }

    /// <summary>All numbers that appear anywhere in the given texts (tool result JSON, the question).</summary>
    public static HashSet<decimal> AllowedNumbers(IEnumerable<string> sources)
    {
        var set = new HashSet<decimal>();
        foreach (var s in sources)
        foreach (Match m in PlainNumberRegex().Matches(NormalizeDigits(s)))
            if (decimal.TryParse(m.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var v))
            {
                set.Add(v);
                set.Add(Math.Abs(v));
            }
        return set;
    }

    /// <summary>Returns the claims in <paramref name="text"/> that no allowed number supports.</summary>
    public static IReadOnlyList<Claim> Ungrounded(string text, IReadOnlyCollection<decimal> allowed) =>
        ExtractClaims(text).Where(c => !Supported(c, allowed)).ToList();

    private static bool Supported(Claim c, IReadOnlyCollection<decimal> allowed)
    {
        if (!c.Percent && c.Multiplier == 1m && c.Decimals == 0 && c.Value is >= 0 and <= FreeIntegerMax) return true;
        var target = c.Value * c.Multiplier;
        var tolerance = 0.5m * Pow10(-c.Decimals) * c.Multiplier + 0.000001m;
        return allowed.Any(a => Math.Abs(a - target) <= tolerance);
    }

    private static decimal Pow10(int exp)
    {
        var r = 1m;
        for (var i = 0; i < Math.Abs(exp); i++) r *= 10m;
        return exp < 0 ? 1m / r : r;
    }

    // 1,234,567.89 | 1234.5 | 200K | 1.2 million | 45% | 3 مليون
    [GeneratedRegex(@"(?<![\w.])(?<num>-?\d{1,3}(?:,\d{3})+(?:\.\d+)?|-?\d+(?:\.\d+)?)(?<suffix>\s?(?:[kKmMbB]n?\b|thousand|million|billion|ألف|الف|مليون|مليار))?(?<pct>\s?%)?")]
    private static partial Regex NumberRegex();

    [GeneratedRegex(@"-?\d+(?:\.\d+)?")]
    private static partial Regex PlainNumberRegex();
}
