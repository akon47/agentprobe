using System.Reflection;

namespace AgentProbe;

/// <summary>Constants and well-known paths shared by every mode.</summary>
internal static class AppInfo
{
    public const string Name = "agentprobe";
    public const int ProtocolVersion = 1;
    public const int DefaultPort = 48650;

    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    /// <summary>
    /// Where this exe was started from. Read once: an update moves the running file aside and puts the new one
    /// under this name, and "the exe" must keep meaning that name, not the file this process happens to run from.
    /// </summary>
    public static string ExePath { get; } = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, Name + ".exe");

    /// <summary>
    /// AGENTPROBE_INSTANCE, together with AGENTPROBE_PORT, runs a second agentprobe beside the normal one on the same
    /// PC: its own background process, state folder and port. The test suite uses it so that it never touches a
    /// session someone is working in. Empty for normal use.
    /// </summary>
    public static string Instance { get; } = InstanceSuffix();

    /// <summary>Per-user state folder. The controller keeps its identity here; the target only uses it across a reboot.</summary>
    public static string StateDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name + Instance);

    public static string PipeName => $"{Name}-{Environment.UserName}{Instance}";

    private static string InstanceSuffix()
    {
        var name = new string((Environment.GetEnvironmentVariable("AGENTPROBE_INSTANCE") ?? "").Where(char.IsAsciiLetterOrDigit).Take(16).ToArray());
        return name.Length == 0 ? "" : "-" + name.ToLowerInvariant();
    }

    public static int Port
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("AGENTPROBE_PORT");
            return int.TryParse(env, out var p) && p is > 0 and < 65536 ? p : DefaultPort;
        }
    }
}
