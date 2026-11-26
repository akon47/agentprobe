using System.Text.RegularExpressions;

namespace AgentProbe.Util;

/// <summary>
/// A log that says the same thing five hundred times costs five hundred lines of an agent's context and tells it
/// one thing. Lines that follow each other and differ only in their numbers (the time, a counter, an id) are
/// printed as the first of them and one line that says how many followed and gives the last one, so the span is
/// known. What is left out is always said in that line, and every tool that folds has <c>--no-fold</c>.
/// </summary>
internal static partial class LineFold
{
    /// <summary>A run shorter than this is printed as it is.</summary>
    private const int MinRun = 4;

    [GeneratedRegex("0[xX][0-9a-fA-F]+|[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}|[0-9]+")]
    private static partial Regex Numbers();

    private static string Shape(string text) => Numbers().Replace(text, "#");

    /// <param name="lines">Each line as it is printed, and the text it is compared by.</param>
    /// <param name="eligible">Which lines may be folded at all, by their text (null: every line).</param>
    public static List<string> Similar(IReadOnlyList<(string shown, string text)> lines, Func<string, bool>? eligible = null)
    {
        var result = new List<string>(lines.Count);
        var i = 0;
        while (i < lines.Count)
        {
            var first = lines[i];
            if (first.text.Trim().Length == 0 || (eligible != null && !eligible(first.text)))
            {
                result.Add(first.shown);
                i++;
                continue;
            }

            var shape = Shape(first.text);
            var identical = true;
            var j = i + 1;
            while (j < lines.Count && Shape(lines[j].text) == shape && (eligible == null || eligible(lines[j].text)))
            {
                identical &= lines[j].text == first.text;
                j++;
            }

            if (j - i < MinRun)
            {
                for (var k = i; k < j; k++) result.Add(lines[k].shown);
            }
            else
            {
                result.Add(first.shown);
                result.Add(identical
                    ? $"  [+{j - i - 1} more lines identical to the one above; --no-fold prints them]"
                    : $"  [+{j - i - 1} more lines like the one above that differ only in numbers; the last one: {Format.Clip(lines[j - 1].shown, 200)}  (--no-fold prints them all)]");
            }

            i = j;
        }

        return result;
    }

    /// <summary>The same for a block of text.</summary>
    public static string Text(string text)
    {
        if (text.Length == 0) return text;
        var lines = text.Split('\n');
        var count = text.EndsWith('\n') ? lines.Length - 1 : lines.Length;
        if (count < MinRun) return text;
        var folded = Similar([.. lines.Take(count).Select(l => (l, l))]);
        return folded.Count == count ? text : string.Join('\n', folded) + "\n";
    }
}
