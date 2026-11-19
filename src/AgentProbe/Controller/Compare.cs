using AgentProbe.Tools;
using AgentProbe.Transport;
using AgentProbe.Util;

namespace AgentProbe.Controller;

/// <summary>
/// "It works on my machine": fingerprints two PCs with <c>envsnap</c> and prints only the differences.
/// Either side may be <c>local</c> (the developer PC), so "the PC where it fails" vs "the PC where it works" is one command.
/// </summary>
internal static class Compare
{
    public static async Task<int> RunAsync(string[] args)
    {
        var a = ArgParser.Parse(args);
        if (a.Positionals.Count is < 1 or > 2)
        {
            Console.Error.WriteLine("""
                usage: agentprobe compare <target-a> [target-b|local] [--dir FOLDER] [--reg KEY] [--grep REGEX]
                  Differences in OS build, locale, drivers, runtimes, environment, PATH, installed programs and services.
                  --dir also compares every file (size + version) under that folder on both PCs, e.g. the app's install folder.
                  With one target, it is compared against this PC.
                """);
            return 2;
        }

        var nameA = a.Positionals[0];
        var nameB = a.Positionals.Count == 2 ? a.Positionals[1] : "local";
        var snapArgs = new List<string>();
        if (a.Get("dir") is { } dir) snapArgs.AddRange(["--dir", dir]);
        if (a.Get("reg") is { } reg) snapArgs.AddRange(["--reg", reg]);

        var results = await Task.WhenAll(SnapAsync(nameA, [.. snapArgs]), SnapAsync(nameB, [.. snapArgs]));
        if (results.Any(r => r == null)) return 3;

        var lines = SystemTools.Diff(results[0]!, results[1]!, nameA, nameB);
        if (a.Get("grep") is { } grep)
        {
            var re = new System.Text.RegularExpressions.Regex(grep, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            lines = lines.Where(l => re.IsMatch(l)).ToList();
        }

        // Group by the section before the first '/', biggest sections last so the important small ones are read first.
        Console.WriteLine($"{lines.Count} differences between {nameA} ({results[0]!.Count} facts) and {nameB} ({results[1]!.Count} facts)   ~ differs   - only {nameA}   + only {nameB}");
        foreach (var g in lines.GroupBy(l => l[2..].Split('/', ':')[0]).OrderBy(g => g.Count()))
        {
            Console.WriteLine($"[{g.Key}] {g.Count()}");
            foreach (var l in g.Take(60)) Console.WriteLine("  " + l);
            if (g.Count() > 60) Console.WriteLine($"  … {g.Count() - 60} more (narrow with --grep \"^. {g.Key}/\")");
        }

        return 0;
    }

    private static async Task<Dictionary<string, string>?> SnapAsync(string target, string[] args)
    {
        var sw = new StringWriter();
        int code;
        if (target.Equals("local", StringComparison.OrdinalIgnoreCase))
        {
            code = await LocalRunner.RunAsync(ToolRegistry.Find("envsnap")!, args, sw, Console.Error, null);
        }
        else
        {
            code = await Cli.DaemonAsync(new PipeMsg { Op = "run", Target = target, Tool = "envsnap", Args = args }, "envsnap", true, sw);
        }

        if (code != 0)
        {
            Console.Error.Write(sw.ToString());
            Console.Error.WriteLine($"compare: could not snapshot {target}.");
            return null;
        }

        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = line.IndexOf('=');
            if (i > 0) d[line[..i]] = line[(i + 1)..].TrimEnd('\r');
        }

        return d;
    }
}
