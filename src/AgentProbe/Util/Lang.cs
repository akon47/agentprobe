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

    private static bool Detect(string? requested)
    {
        var name = requested ?? Environment.GetEnvironmentVariable("AGENTPROBE_LANG") ?? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return name.StartsWith("ko", StringComparison.OrdinalIgnoreCase);
    }
}
