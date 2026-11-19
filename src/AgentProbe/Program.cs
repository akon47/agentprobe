using System.Runtime.InteropServices;
using AgentProbe.Controller;
using AgentProbe.Target;

namespace AgentProbe;

/// <summary>
/// One exe, three roles:
/// <list type="bullet">
/// <item><c>agentprobe share</c> (or a double-click on a PC that was never a developer PC): let an agent control THIS PC.</item>
/// <item><c>agentprobe &lt;command&gt;</c>: the CLI on the developer PC; it talks to a background daemon.</item>
/// <item><c>agentprobe daemon</c>: that background process, started automatically.</item>
/// </list>
/// </summary>
internal static partial class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0) return await NoArgumentsAsync();
            return args[0].ToLowerInvariant() switch
            {
                "share" => await ShareOrHomeAsync(args[1..]),
                "daemon" => await Daemon.RunAsync(),
                "mcp" => await McpServer.RunAsync(),
                _ => await Cli.RunAsync(args),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"agentprobe: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// No arguments means a double-click (or someone trying it out). Pick the role that fits this PC:
    /// a PC that has acted as a developer PC shows the start screen (dashboard link, what to give an agent);
    /// any other PC offers itself for testing, and pressing D there switches it to the developer role.
    /// </summary>
    private static async Task<int> NoArgumentsAsync()
    {
        var isDeveloperPc = File.Exists(Path.Combine(AppInfo.StateDir, "controller.key"));
        if (!isDeveloperPc) return await ShareOrHomeAsync([]);
        // Piped (an agent or a script trying the bare command): the command list is the useful answer there.
        if (Console.IsOutputRedirected) return await Cli.RunAsync(["help"]);
        return await Home.ShowAsync(openBrowser: false, interactive: true, ownWindow: OwnsConsole());
    }

    /// <summary>Runs share mode; if the person says "this is my developer PC" there, continue as one.</summary>
    private static async Task<int> ShareOrHomeAsync(string[] args)
    {
        var code = await ShareHost.RunAsync(args);
        return code == ShareHost.SwitchToDeveloper ? await Home.ShowAsync(openBrowser: false, interactive: true, ownWindow: true) : code;
    }

    /// <summary>True when this process is the only one attached to its console, i.e. it was started by a double-click.</summary>
    private static unsafe bool OwnsConsole()
    {
        var list = stackalloc uint[4];
        return GetConsoleProcessList(list, 4) == 1;
    }

    [LibraryImport("kernel32.dll")]
    private static unsafe partial uint GetConsoleProcessList(uint* list, uint count);
}
