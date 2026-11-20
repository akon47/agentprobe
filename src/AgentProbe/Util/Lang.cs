using System.Globalization;

namespace AgentProbe.Util;

/// <summary>
/// Language of the text a PERSON reads on the target PC (approval prompt, banner, status lines): Korean or English,
/// following the Windows display language. Output meant for agents and the activity log stay English so they
/// read the same on every machine.
/// </summary>
internal static class Lang
{
    public static bool Korean { get; private set; } = Detect(null);

    public static void Set(string? requested) => Korean = Detect(requested);

    public static string T(string english, string korean) => Korean ? korean : english;

    /// <summary>
    /// True when a person reads this process's output: it goes to a console, or the start screen runs the command
    /// for the person at it (AGENTPROBE_PERSON). An agent reads through a pipe.
    /// </summary>
    public static bool Person { get; } = Environment.GetEnvironmentVariable("AGENTPROBE_PERSON") == "1" || !Console.IsOutputRedirected;

    /// <summary>
    /// For what the command line itself says (guidance, errors, help): Korean when a person reads it on a Korean
    /// Windows, English for agents always. What a tool reports from the target stays English either way.
    /// </summary>
    public static string P(string english, string korean) => Korean && Person ? korean : english;

    private static bool Detect(string? requested)
    {
        var name = requested ?? Environment.GetEnvironmentVariable("AGENTPROBE_LANG") ?? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return name.StartsWith("ko", StringComparison.OrdinalIgnoreCase);
    }
}
