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

    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, Name + ".exe");

    /// <summary>Per-user state folder. The controller keeps its identity here; the target only uses it across a reboot.</summary>
    public static string StateDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name);

    public static string PipeName => $"{Name}-{Environment.UserName}";

    public static int Port
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("AGENTPROBE_PORT");
            return int.TryParse(env, out var p) && p is > 0 and < 65536 ? p : DefaultPort;
        }
    }
}
