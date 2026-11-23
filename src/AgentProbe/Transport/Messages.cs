using System.Text.Json.Serialization;

namespace AgentProbe.Transport;

/// <summary>
/// Every message between controller and target. A flat bag keeps the protocol tiny:
/// adding a diagnostic tool never needs a new message type, just a new tool name.
/// </summary>
internal sealed class Msg
{
    /// <summary>hello | run | cancel | upload | out | exit | blob</summary>
    public string Type { get; set; } = "";

    /// <summary>Request id chosen by the controller; replies carry the same id.</summary>
    public int Id { get; set; }

    public string? Tool { get; set; }

    public string[]? Args { get; set; }

    public string? Text { get; set; }

    /// <summary>For <c>out</c>: true when the text belongs to stderr.</summary>
    public bool Err { get; set; }

    public int Code { get; set; }

    public string? Path { get; set; }

    public string? Name { get; set; }

    public long Size { get; set; }

    public bool Zip { get; set; }

    /// <summary>
    /// For <c>run</c>: a housekeeping request (a dashboard refresh), not something a person or agent asked for.
    /// It is kept out of the session history and does not touch the on-screen activity.
    /// </summary>
    public bool Background { get; set; }

    public Dictionary<string, string>? Info { get; set; }
}

/// <summary>CLI ⇄ local daemon messages over a named pipe, one JSON object per line.</summary>
internal sealed class PipeMsg
{
    // request
    public string? Op { get; set; }

    public string? Target { get; set; }

    public string? Tool { get; set; }

    public string[]? Args { get; set; }

    public string? LocalPath { get; set; }

    public string? RemotePath { get; set; }

    public bool Wait { get; set; }

    public bool Background { get; set; }

    public int TimeoutSec { get; set; }

    /// <summary>"ko" when a person on a Korean Windows asked: the daemon's own guidance and errors are then in Korean.</summary>
    public string? Lang { get; set; }

    // events: out | err | exit | saved | error | info
    public string? Ev { get; set; }

    public string? Text { get; set; }

    public int Code { get; set; }
}

/// <summary>What a target writes to disk before a reboot so it can come back without asking again.</summary>
internal sealed class ResumeState
{
    /// <summary>"" for a reboot; "update" when agentprobe restarted itself from a new exe (no reboot happened).</summary>
    public string Reason { get; set; } = "";

    /// <summary>The number of the newest run when the state was written; the next instance counts on from it.</summary>
    public int LastRun { get; set; }

    public string ControllerKey { get; set; } = "";

    public string ControllerHost { get; set; } = "";

    public int ControllerPort { get; set; }

    public string TargetKey { get; set; } = "";

    public string WorkDir { get; set; } = "";

    /// <summary>Sharing was started with --minimized: the next instance keeps its window out of the way too.</summary>
    public bool Minimized { get; set; }

    /// <summary>Shown in the banner and the log after the reboot, before discovery has seen the developer PC again.</summary>
    public string ControllerName { get; set; } = "";

    /// <summary>The last commands before the reboot (see <c>TargetRuntime.ExportHistory</c>), so `history` still shows where work stopped.</summary>
    public List<string> History { get; set; } = [];

    public DateTime ExpiresUtc { get; set; }

    public bool ScheduledTask { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault)]
[JsonSerializable(typeof(Msg))]
[JsonSerializable(typeof(PipeMsg))]
[JsonSerializable(typeof(ResumeState))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(List<Dictionary<string, string>>))]
internal sealed partial class JsonCtx : JsonSerializerContext
{
}
