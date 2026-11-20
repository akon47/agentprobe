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
                "daemon" => await Daemon.RunAsync(args[1..]),
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
    /// No arguments means a double-click (or someone trying it out). The same exe is both sides, and a wrong guess
    /// leaves two PCs waiting for each other, so the person is asked which one this PC is. Enter takes what this PC
    /// was last time: a PC that has acted as a developer PC shows the start screen, any other PC offers itself.
    /// Without a console to ask in, that default is used as it is.
    /// </summary>
    private static async Task<int> NoArgumentsAsync()
    {
        var isDeveloperPc = File.Exists(Path.Combine(AppInfo.StateDir, "controller.key"));
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            if (!isDeveloperPc) return await ShareOrHomeAsync([]);
            // Piped (an agent or a script trying the bare command): the command list is the useful answer there.
            if (Console.IsOutputRedirected) return await Cli.RunAsync(["help"]);
            return await Home.ShowAsync(openBrowser: false, interactive: true, ownWindow: OwnsConsole());
        }

        var ownWindow = OwnsConsole();
        switch (AskRole(isDeveloperPc))
        {
            case '1':
                ShareHost.RoleChosen = true;
                return await ShareOrHomeAsync([]);
            case '2':
                return await Home.ShowAsync(openBrowser: false, interactive: true, ownWindow: ownWindow);
            default:
                return 0;
        }
    }

    /// <summary>Returns '1' (share this PC), '2' (developer PC) or '\0' (Esc: do nothing).</summary>
    private static char AskRole(bool isDeveloperPc)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        var fallback = isDeveloperPc ? '2' : '1';
        var title = $"agentprobe {AppInfo.Version}";
        try
        {
            if (OwnsConsole()) Console.Title = AppInfo.Name;
        }
        catch (IOException)
        {
            // no console window to name
        }

        var question = Util.Lang.T("How will this PC be used?", "이 PC를 어떻게 쓸까요?");
        var share = new MenuOption(Util.Lang.T("Share this PC", "이 PC 공유"), Util.Lang.T("a developer PC debugs and tests this PC", "개발 PC가 이 PC를 디버깅하고 테스트"));
        var develop = new MenuOption(Util.Lang.T("Use as developer PC", "개발 PC로 사용"), Util.Lang.T("your AI agent runs here and controls shared PCs", "AI 에이전트가 여기서 공유된 PC를 제어"));
        if (ConsoleMenu.Choose(title, question, [share, develop], fallback - '1') is { } picked)
            return picked switch { 0 => '1', 1 => '2', _ => '\0' };

        // A console that cannot show the clickable menu: the same question as plain text.
        Console.WriteLine(title);
        Console.WriteLine();
        Console.WriteLine(question);
        Console.WriteLine();
        Console.WriteLine($"  [1] {share.Label}  -  {share.Detail}");
        Console.WriteLine($"  [2] {develop.Label}  -  {develop.Detail}");
        Console.WriteLine();
        Console.Write(Util.Lang.T($"Press 1 or 2  (Enter = {fallback})   Esc closes  ", $"1 또는 2를 누르세요  (Enter = {fallback})   Esc: 닫기  "));
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            var choice = key.Key switch
            {
                ConsoleKey.Enter => fallback,
                ConsoleKey.Escape => '\0',
                _ => key.KeyChar is '1' or '2' ? key.KeyChar : ' ',
            };
            if (choice == ' ') continue;
            Console.WriteLine(choice == '\0' ? "" : choice.ToString());
            Console.WriteLine();
            return choice;
        }
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
