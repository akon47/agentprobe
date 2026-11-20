using System.Text;
using System.Text.Json;
using AgentProbe.Tools;
using AgentProbe.Transport;

namespace AgentProbe.Controller;

/// <summary>Turns one typed command line ("ps --top 5", "exec Get-Date") into a tool name and arguments.</summary>
internal static class CommandLine
{
    public static (Tool? tool, string[] args, string? error) Parse(string line)
    {
        var tokens = Tokenize(line);
        if (tokens.Count > 1 && tokens[0].text.Equals(AppInfo.Name, StringComparison.OrdinalIgnoreCase)) tokens.RemoveAt(0);
        if (tokens.Count == 0) return (null, [], "Type a command, for example: triage");
        var name = tokens[0].text.ToLowerInvariant();
        if (ToolRegistry.Find(name) is not { } tool)
            return (null, [], $"'{name}' is not a command that runs on a target. Try: triage, ps, top, proc <name>, exec <powershell>, events, windows ...");

        if (tool.Name is "exec" or "start")
        {
            // Options first, then the rest of the line verbatim so quotes reach the shell untouched.
            var options = new List<string>();
            var i = 1;
            while (i + 1 < tokens.Count && tokens[i].text is "--shell" or "--cwd" or "--timeout" or "--name")
            {
                options.Add(tokens[i].text);
                options.Add(tokens[i + 1].text);
                i += 2;
            }

            if (i >= tokens.Count) return (tool, [], $"{tool.Name}: no command given.");
            if (tool.Name == "start" && tokens[i].text == "--exe")
            {
                // start --exe <path> [args]: the program itself, with the words after its path as its arguments.
                if (i + 1 >= tokens.Count) return (tool, [], "start: --exe needs the path of a program on the target.");
                return (tool, [.. options, "--exe", tokens[i + 1].text, "--", .. tokens.Skip(i + 2).Select(t => t.text)], null);
            }

            options.Add("--script");
            options.Add(line[tokens[i].start..].Trim());
            return (tool, [.. options], null);
        }

        return (tool, [.. tokens.Skip(1).Select(t => t.text)], null);
    }

    /// <summary>Splits into words, honoring double and single quotes; remembers where each word starts.</summary>
    internal static List<(string text, int start)> Tokenize(string line)
    {
        var result = new List<(string, int)>();
        var sb = new StringBuilder();
        var start = -1;
        var quote = '\0';
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quote != '\0')
            {
                if (ch == quote) quote = '\0';
                else sb.Append(ch);
            }
            else if (ch is '"' or '\'')
            {
                if (start < 0) start = i;
                quote = ch;
            }
            else if (char.IsWhiteSpace(ch))
            {
                if (start >= 0)
                {
                    result.Add((sb.ToString(), start));
                    sb.Clear();
                    start = -1;
                }
            }
            else
            {
                if (start < 0) start = i;
                sb.Append(ch);
            }
        }

        if (start >= 0) result.Add((sb.ToString(), start));
        return result;
    }
}

/// <summary>
/// <c>agentprobe mcp</c>: a Model Context Protocol server over stdio, for agents that are set up through MCP.
/// <para>
/// It is deliberately thin. Tool definitions are loaded into an agent's context on every turn, so instead of one MCP
/// tool per diagnostic there are four: list targets, run any agentprobe command line, take a screenshot (returned as
/// an image), and read the guide. The server's instructions also tell the agent where the exe is, so an agent with a
/// shell can switch to the CLI, where output can be piped, filtered and saved.
/// </para>
/// </summary>
internal static class McpServer
{
    private static readonly SemaphoreSlim WriteLock = new(1, 1);
    private static Stream _stdout = Stream.Null;

    public static async Task<int> RunAsync()
    {
        _stdout = Console.OpenStandardOutput();
        using var reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var pending = new List<Task>();
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.Length == 0) continue;
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                await ReplyAsync(null, w => Error(w, -32700, "Parse error"));
                continue;
            }

            // Calls may take minutes (a remote command): never block the read loop on them.
            pending.Add(Task.Run(async () =>
            {
                using (doc) await HandleAsync(doc.RootElement);
            }));
            pending.RemoveAll(t => t.IsCompleted);
        }

        // The client closed stdin: let calls that are still running send their answers before exiting.
        await Task.WhenAll(pending);
        return 0;
    }

    private static async Task HandleAsync(JsonElement msg)
    {
        var hasId = msg.TryGetProperty("id", out var id);
        var method = msg.TryGetProperty("method", out var m) ? m.GetString() : null;
        if (method == null || !hasId) return; // a response or a notification: nothing to answer
        msg.TryGetProperty("params", out var p);
        try
        {
            switch (method)
            {
                case "initialize":
                    var version = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("protocolVersion", out var v) ? v.GetString() : null;
                    await ReplyAsync(id, w => Result(w, r =>
                    {
                        r.WriteString("protocolVersion", version ?? "2025-06-18");
                        r.WriteStartObject("capabilities");
                        r.WriteStartObject("tools");
                        r.WriteEndObject();
                        r.WriteEndObject();
                        r.WriteStartObject("serverInfo");
                        r.WriteString("name", AppInfo.Name);
                        r.WriteString("version", AppInfo.Version);
                        r.WriteEndObject();
                        r.WriteString("instructions", Instructions());
                    }));
                    break;
                case "ping":
                    await ReplyAsync(id, w => Result(w, _ => { }));
                    break;
                case "tools/list":
                    await ReplyAsync(id, w => Result(w, WriteTools));
                    break;
                case "tools/call":
                    await CallAsync(id, p);
                    break;
                default:
                    await ReplyAsync(id, w => Error(w, -32601, $"Method not found: {method}"));
                    break;
            }
        }
        catch (Exception ex)
        {
            await ReplyAsync(id, w => Error(w, -32603, ex.Message));
        }
    }

    private static string Instructions()
    {
        var exe = $"\"{AppInfo.ExePath}\"";
        return "agentprobe debugs and tests other Windows PCs that a person shared with this developer PC. " +
               "Everything you run is shown on that PC's screen and logged there.\n" +
               $"If you can run shell commands, prefer the CLI over these tools: {exe} <command>. It is the same tool, " +
               "and its output can be piped, filtered and saved to files instead of filling your context.\n" +
               $"Start with `{exe} guide` (or the `guide` tool), then `list`, then `triage`. " +
               "Typical commands: triage, ps, top, proc <name>, threads <pid>, modules <pid>, handles <pid>, mem, sys, events, crashes, " +
               "dbg, watch start <name> / watch, deps <exe>, compare <target> local, exec '<powershell>', start/logs/kill, push/pull, " +
               "windows, screenshot, click/type/key, wait, history, reboot --wait, end. `help <command>` shows options.";
    }

    private static void WriteTools(Utf8JsonWriter w)
    {
        w.WriteStartArray("tools");
        Tool(w, "list_targets", "List the Windows PCs currently shared with this developer PC (name, user, rights, state, last command).", s =>
        {
            Prop(s, "all", "boolean", "Also show PCs that went offline, with when they were last seen.");
        });
        Tool(w, "run",
            "Run one agentprobe command on a shared PC and return its compact text output. " +
            "Examples: 'triage', 'ps --top 10', 'proc MyApp', 'events --since 30m', 'watch start MyApp', 'exec Get-Service Spooler', 'help exec'. " +
            "Call `guide` first to learn which command answers which question.", s =>
        {
            Prop(s, "command", "string", "The command line, without the leading 'agentprobe'.");
            Prop(s, "target", "string", "Target name (see list_targets). Omit when only one PC is shared. 'all' or 'LAB-*' run on several and group identical results.");
        }, "command");
        Tool(w, "screenshot", "Capture a shared PC's screen and return it as an image. Use `run` with 'windows' first when you only need to know which windows exist.", s =>
        {
            Prop(s, "target", "string", "Target name. Omit when only one PC is shared.");
            Prop(s, "window", "string", "Capture just the window whose title contains this text.");
            Prop(s, "scale", "number", "Shrink factor, 0.1 to 1. Default 0.5; smaller images cost less to read.");
        });
        Tool(w, "guide", "How to use agentprobe: which command answers which question, typical debugging sessions, exit codes.", _ => { });
        w.WriteEndArray();
    }

    private static void Tool(Utf8JsonWriter w, string name, string description, Action<Utf8JsonWriter> properties, params string[] required)
    {
        w.WriteStartObject();
        w.WriteString("name", name);
        w.WriteString("description", description);
        w.WriteStartObject("inputSchema");
        w.WriteString("type", "object");
        w.WriteStartObject("properties");
        properties(w);
        w.WriteEndObject();
        w.WriteStartArray("required");
        foreach (var r in required) w.WriteStringValue(r);
        w.WriteEndArray();
        w.WriteEndObject();
        w.WriteEndObject();
    }

    private static void Prop(Utf8JsonWriter w, string name, string type, string description)
    {
        w.WriteStartObject(name);
        w.WriteString("type", type);
        w.WriteString("description", description);
        w.WriteEndObject();
    }

    private static string? Arg(JsonElement args, string name)
        => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static async Task CallAsync(JsonElement id, JsonElement p)
    {
        var name = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("name", out var n) ? n.GetString() : null;
        var args = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("arguments", out var a) ? a : default;
        switch (name)
        {
            case "guide":
                await TextAsync(id, Cli.Guide(), false);
                return;

            case "list_targets":
            {
                var all = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("all", out var v) && v.ValueKind == JsonValueKind.True;
                var (_, text) = await DaemonAsync(new PipeMsg { Op = "list", Args = all ? ["--all"] : [] });
                await TextAsync(id, text, false);
                return;
            }

            case "run":
            {
                var command = Arg(args, "command") ?? "";
                var first = command.TrimStart().Split(' ', 2)[0].ToLowerInvariant();
                if (first is "guide")
                {
                    await TextAsync(id, Cli.Guide(), false);
                    return;
                }

                if (first is "list" or "targets")
                {
                    var (_, listText) = await DaemonAsync(new PipeMsg { Op = "list", Args = command.Contains("--all", StringComparison.Ordinal) ? ["--all"] : [] });
                    await TextAsync(id, listText, false);
                    return;
                }

                if (first is "help")
                {
                    var topic = command.TrimStart().Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).FirstOrDefault();
                    var tool = topic != null ? ToolRegistry.Find(topic) : null;
                    await TextAsync(id, tool != null ? $"{tool.Name} - {tool.Summary}\n\n{tool.Usage.TrimEnd()}" : ToolRegistry.HelpList() + "\nhelp <command> shows options.", false);
                    return;
                }

                var (parsed, toolArgs, error) = CommandLine.Parse(command);
                if (parsed == null || error != null)
                {
                    await TextAsync(id, error ?? "unknown command", true);
                    return;
                }

                if (parsed.Name == "push")
                {
                    if (toolArgs.Length is < 1 or > 2)
                    {
                        await TextAsync(id, "usage: push <local-path> [remote-path]", true);
                        return;
                    }

                    var local = Path.GetFullPath(toolArgs[0]);
                    var (pushCode, pushText) = await DaemonAsync(new PipeMsg { Op = "push", Target = Arg(args, "target"), LocalPath = local, RemotePath = toolArgs.Length == 2 ? toolArgs[1] : Path.GetFileName(local.TrimEnd('\\', '/')) });
                    await TextAsync(id, pushText, pushCode != 0);
                    return;
                }

                // Files a command sends back (dump, pull, wpr stop) land in the agent's working folder, like the CLI does.
                var (code, text) = await DaemonAsync(new PipeMsg { Op = "run", Target = Arg(args, "target"), Tool = parsed.Name, Args = toolArgs, LocalPath = Environment.CurrentDirectory });
                text = Limit(text, parsed.Name);
                if (code != 0) text = text.TrimEnd() + $"\n[exit {code}]";
                // For exec/start a non-zero code is the remote program's own result, not a failure of this tool.
                await TextAsync(id, text, code == 75 || (parsed.Name is not ("exec" or "start") && code is 2 or 3));
                return;
            }

            case "screenshot":
            {
                var file = Path.Combine(Path.GetTempPath(), $"{AppInfo.Name}-mcp-{Guid.NewGuid():N}.png");
                var shotArgs = new List<string> { "--scale", (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("scale", out var sc) && sc.TryGetDouble(out var scale) ? Math.Clamp(scale, 0.1, 1) : 0.5).ToString(System.Globalization.CultureInfo.InvariantCulture) };
                if (Arg(args, "window") is { } window) shotArgs.AddRange(["--window", window]);
                try
                {
                    var (code, text) = await DaemonAsync(new PipeMsg { Op = "run", Target = Arg(args, "target"), Tool = "screenshot", Args = [.. shotArgs], LocalPath = file });
                    if (code != 0 || !File.Exists(file))
                    {
                        await TextAsync(id, text, true);
                        return;
                    }

                    var png = Convert.ToBase64String(await File.ReadAllBytesAsync(file));
                    var note = string.Join('\n', text.Split('\n').Where(l => l.Length > 0 && !l.StartsWith("saved:", StringComparison.Ordinal)));
                    await ReplyAsync(id, w => Result(w, r =>
                    {
                        r.WriteStartArray("content");
                        r.WriteStartObject();
                        r.WriteString("type", "image");
                        r.WriteString("data", png);
                        r.WriteString("mimeType", "image/png");
                        r.WriteEndObject();
                        r.WriteStartObject();
                        r.WriteString("type", "text");
                        r.WriteString("text", note);
                        r.WriteEndObject();
                        r.WriteEndArray();
                    }));
                }
                finally
                {
                    File.Delete(file);
                }

                return;
            }

            default:
                await ReplyAsync(id, w => Error(w, -32602, $"Unknown tool: {name}"));
                return;
        }
    }

    private static async Task<(int code, string text)> DaemonAsync(PipeMsg request)
    {
        var sw = new StringWriter();
        var code = await Cli.DaemonAsync(request, request.Tool ?? request.Op ?? "", true, sw);
        return (code, sw.ToString());
    }

    /// <summary>The same cap the CLI applies: first 200 and last 100 lines, with everything saved to a local file.</summary>
    private static string Limit(string text, string tool)
    {
        var lines = text.Split('\n');
        if (lines.Length <= 320) return text;
        string? path = null;
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), AppInfo.Name, "out");
            Directory.CreateDirectory(dir);
            path = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}-{tool}-{Environment.ProcessId}.txt");
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }
        catch (IOException)
        {
            path = null;
        }

        return string.Join('\n', lines.Take(200)) +
               $"\n[... {lines.Length - 300} lines omitted. Full output ({lines.Length} lines): {path ?? "(could not be saved)"} - read or search that file, or rerun with --grep/--tail ...]\n" +
               string.Join('\n', lines.TakeLast(100));
    }

    private static Task TextAsync(JsonElement id, string text, bool isError) => ReplyAsync(id, w => Result(w, r =>
    {
        r.WriteStartArray("content");
        r.WriteStartObject();
        r.WriteString("type", "text");
        r.WriteString("text", text.Length > 0 ? text : "(no output)");
        r.WriteEndObject();
        r.WriteEndArray();
        if (isError) r.WriteBoolean("isError", true);
    }));

    private static void Result(Utf8JsonWriter w, Action<Utf8JsonWriter> body)
    {
        w.WriteStartObject("result");
        body(w);
        w.WriteEndObject();
    }

    private static void Error(Utf8JsonWriter w, int code, string message)
    {
        w.WriteStartObject("error");
        w.WriteNumber("code", code);
        w.WriteString("message", message);
        w.WriteEndObject();
    }

    private static async Task ReplyAsync(JsonElement? id, Action<Utf8JsonWriter> body)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
            if (id is { } value)
            {
                w.WritePropertyName("id");
                value.WriteTo(w);
            }
            else
            {
                w.WriteNull("id");
            }

            body(w);
            w.WriteEndObject();
        }

        ms.WriteByte((byte)'\n');
        await WriteLock.WaitAsync();
        try
        {
            await _stdout.WriteAsync(ms.GetBuffer().AsMemory(0, (int)ms.Length));
            await _stdout.FlushAsync();
        }
        finally
        {
            WriteLock.Release();
        }
    }
}
