using System.Globalization;
using System.Text.RegularExpressions;

namespace BDM.Core.Batch;

public static class BatchPattern
{
    const long MaxUrls = 10_000;
    static readonly Regex Group = new(@"\[([0-9A-Za-z]+)-([0-9A-Za-z]+)\]", RegexOptions.Compiled);

    /// <summary>
    /// Expands `[001-100]` (zero-padded when both ends have the same width and start with 0) and `[a-z]` groups.
    /// Several groups multiply, leftmost varying slowest. Anything else in brackets (e.g. an IPv6 host) is literal.
    /// </summary>
    public static IReadOnlyList<Uri> Expand(string pattern)
    {
        var parts = new List<List<string>>(); // alternating literal / group options
        long total = 1;
        var pos = 0;
        foreach (Match m in Group.Matches(pattern))
        {
            var options = Options(m.Groups[1].Value, m.Groups[2].Value);
            if (options == null) continue;
            parts.Add(new() { pattern[pos..m.Index] });
            parts.Add(options);
            pos = m.Index + m.Length;
            total *= options.Count;
            if (total > MaxUrls) throw new ArgumentException($"The pattern expands to more than {MaxUrls.ToString("N0", CultureInfo.InvariantCulture)} addresses.");
        }
        parts.Add(new() { pattern[pos..] });

        var results = new List<string> { "" };
        foreach (var options in parts)
            results = results.SelectMany(prefix => options.Select(o => prefix + o)).ToList(); // leftmost group varies slowest
        return results.Select(s => new Uri(s)).ToList();
    }

    static List<string>? Options(string from, string to)
    {
        if (long.TryParse(from, out var a) && long.TryParse(to, out var b))
        {
            if (b < a) return null;
            if (b - a + 1 > MaxUrls) throw new ArgumentException($"The pattern expands to more than {MaxUrls.ToString("N0", CultureInfo.InvariantCulture)} addresses.");
            var width = from.Length == to.Length && from[0] == '0' ? from.Length : 0;
            return Enumerable.Range(0, (int)(b - a + 1)).Select(i => (a + i).ToString().PadLeft(width, '0')).ToList();
        }
        if (from.Length == 1 && to.Length == 1 && char.IsLetter(from[0]) && char.IsLetter(to[0]) && to[0] >= from[0])
            return Enumerable.Range(from[0], to[0] - from[0] + 1).Select(c => ((char)c).ToString()).ToList();
        return null;
    }
}
