using System.Text;
using AgentProbe.Target;

namespace AgentProbe.Tools;

/// <summary>A command that runs on the target PC and answers with compact text.</summary>
internal sealed record Tool(string Name, string Group, string Summary, string Usage, Func<ToolContext, string[], Task<int>> Run)
{
    /// <summary>Chatty read-only tools are not written to the on-screen activity log one by one.</summary>
    public bool Quiet { get; init; }

    /// <summary>
    /// For a quiet tool that has one form which does change the PC (<c>deps --run</c> starts a program): true
    /// for the arguments of that form, which is then logged and counted like any other command.
    /// </summary>
    public Func<string[], bool>? LoudWhen { get; init; }

    /// <summary>May this call stay out of the activity log's command lines?</summary>
    public bool IsQuiet(string[] args) => Quiet && LoudWhen?.Invoke(args) != true;
}

/// <summary>Everything a tool needs: where to write, when to stop, and the per-run state of this target.</summary>
internal sealed class ToolContext
{
    public required TextWriter Out { get; init; }

    public required TextWriter Err { get; init; }

    public required CancellationToken Ct { get; init; }

    public required TargetRuntime Runtime { get; init; }

    /// <summary>Sends a file back to the developer PC (name hint, local path, is-zip, delete-after).</summary>
    public required Func<string, string, bool, bool, Task> SendFile { get; init; }

    public int Fail(string message)
    {
        Err.WriteLine(message);
        return 2;
    }
}

/// <summary>All tools, in help order.</summary>
internal static class ToolRegistry
{
    private static readonly Lazy<List<Tool>> AllTools = new(() =>
    [
        .. ExecTools.Tools,
        .. FileTools.Tools,
        .. ProcessTools.Tools,
        .. SystemTools.Tools,
        .. DeviceTools.Tools,
        .. SensorTools.Tools,
        .. NetTools.Tools,
        .. FirewallTools.Tools,
        .. EventTools.Tools,
        .. BootHistory.Tools,
        .. DebugOutputTools.Tools,
        .. DepsTool.Tools,
        .. DumpTool.Tools,
        .. HeapTools.Tools,
        .. InspectTools.Tools,
        .. AccessTraceTools.Tools,
        .. TraceTools.Tools,
        .. UiTools.Tools,
        .. UiaTools.Tools,
        .. WatchTools.Tools,
        .. WaitTool.Tools,
        .. SessionTools.Tools,
    ]);

    public static IReadOnlyList<Tool> All => AllTools.Value;

    public static Tool? Find(string name) => All.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static string HelpList()
    {
        var sb = new StringBuilder();
        foreach (var g in All.GroupBy(t => t.Group))
        {
            sb.Append(g.Key).Append(":\n");
            foreach (var t in g) sb.Append("  ").Append(t.Name.PadRight(11)).Append(t.Summary).Append('\n');
        }

        return sb.ToString();
    }
}

/// <summary>Text writer that forwards to a callback; used for a tool's stdout and stderr.</summary>
internal sealed class CallbackWriter : TextWriter
{
    private readonly Action<string> write;

    public CallbackWriter(Action<string> write)
    {
        this.write = write;
        NewLine = "\n"; // the property also updates the string WriteLine uses; the CoreNewLine field alone does not
    }

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value) => write(value.ToString());

    public override void Write(string? value)
    {
        if (!string.IsNullOrEmpty(value)) write(value);
    }

    public override void Write(char[] buffer, int index, int count)
    {
        if (count > 0) write(new string(buffer, index, count));
    }
}
