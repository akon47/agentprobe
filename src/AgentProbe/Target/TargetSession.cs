using System.Collections.Concurrent;
using System.IO.Compression;
using System.Threading.Channels;
using AgentProbe.Tools;
using AgentProbe.Transport;
using AgentProbe.Util;

namespace AgentProbe.Target;

/// <summary>One live connection to a developer PC: receives requests, runs tools, streams results back.</summary>
internal sealed class TargetSession(SecureChannel channel, TargetRuntime runtime, string controllerName, Action<string> activity)
{
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _running = new();
    private readonly ConcurrentDictionary<int, Upload> _uploads = new();
    private volatile bool _alive = true;

    public async Task RunAsync(Dictionary<string, string> hello, CancellationToken ct)
    {
        await channel.SendAsync(new Msg { Type = "hello", Info = hello }, ct);
        using var statCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = Task.Run(() => SendStatsAsync(statCts.Token), CancellationToken.None);
        try
        {
            while (await channel.ReceiveAsync(ct) is { } frame)
            {
                if (frame.Message is { } m)
                {
                    switch (m.Type)
                    {
                        case "run":
                            _ = Task.Run(() => HandleRunAsync(m), CancellationToken.None);
                            break;
                        case "cancel":
                            if (_running.TryGetValue(m.Id, out var cts)) cts.Cancel();
                            if (_uploads.TryRemove(m.Id, out var cancelled)) cancelled.Abort(); // a push that was interrupted: drop its temp file
                            break;
                        case "upload":
                            BeginUpload(m);
                            break;
                        case "ping":
                            await channel.SendAsync(new Msg { Type = "pong", Id = m.Id }, ct);
                            break;
                    }
                }
                else if (frame.Data != null)
                {
                    await HandleDataAsync(frame);
                }
            }
        }
        finally
        {
            _alive = false;
            statCts.Cancel();
            foreach (var u in _uploads.Values) u.Abort();
        }
    }

    /// <summary>A heartbeat with CPU and RAM load every few seconds, for the developer PC's dashboard.</summary>
    private async Task SendStatsAsync(CancellationToken ct)
    {
        Native.Win32.GetSystemTimes(out var idle0, out var kernel0, out var user0);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(5000, ct);
                Native.Win32.GetSystemTimes(out var idle1, out var kernel1, out var user1);
                var total = (kernel1 - kernel0) + (user1 - user0);
                var cpu = total > 0 ? 100.0 * (total - (idle1 - idle0)) / total : 0;
                (idle0, kernel0, user0) = (idle1, kernel1, user1);
                var info = new Dictionary<string, string>
                {
                    ["cpu"] = cpu.ToString("0", System.Globalization.CultureInfo.InvariantCulture),
                    ["mem"] = Tools.SystemTools.Memory().dwMemoryLoad.ToString(),
                };
                if (!await TrySendAsync(new Msg { Type = "stat", Info = info })) return;
            }
        }
        catch (OperationCanceledException)
        {
            // session over
        }
    }

    private async Task HandleRunAsync(Msg m)
    {
        var args = m.Args ?? [];
        var tool = ToolRegistry.Find(m.Tool ?? "");
        if (tool == null)
        {
            await TrySendAsync(new Msg { Type = "out", Id = m.Id, Err = true, Text = $"unknown command '{m.Tool}'. Run `agentprobe help`.\n" });
            await TrySendAsync(new Msg { Type = "exit", Id = m.Id, Code = 127 });
            return;
        }

        // Background requests (dashboard refreshes) are not part of the session's story:
        // no history entry, no line in the activity log, no change to the banner.
        // The developer PC asks for this, but the target decides: only read-only tools may run unlogged, so nothing
        // that changes this PC (exec, kill, input, files...) can ever be kept out of the activity log.
        var background = m.Background && (tool.Quiet || (tool.Name == "screenshot" && args.Contains("--thumb")));
        var record = background ? new RunRecord { Tool = tool.Name, Args = args } : runtime.NewRun(tool.Name, args, controllerName);
        if (!background)
        {
            await TrySendAsync(new Msg { Type = "started", Id = m.Id, Code = record.No });
            if (!tool.Quiet) runtime.Audit.Write($"#{record.No} {Format.Clip(record.CommandLine, 300)}");
            activity(Format.Clip(record.CommandLine, 70));
        }

        using var cts = new CancellationTokenSource();
        _running[m.Id] = cts;
        var sink = new OutputSink(this, m.Id, record);
        var ctx = new ToolContext
        {
            Out = new CallbackWriter(s => sink.Write(s, false)),
            Err = new CallbackWriter(s => sink.Write(s, true)),
            Ct = cts.Token,
            Runtime = runtime,
            SendFile = (name, path, zip, delete) => SendFileAsync(m.Id, name, path, zip, delete, sink),
        };

        var code = 1;
        try
        {
            try
            {
                code = await tool.Run(ctx, args);
            }
            catch (OperationCanceledException)
            {
                code = 130;
            }
            catch (Exception ex)
            {
                sink.Write($"{tool.Name}: {ex.GetType().Name}: {ex.Message}\n", true);
                code = 1;
            }

            _running.TryRemove(m.Id, out _);
            await sink.CompleteAsync();
        }
        finally
        {
            // Whatever happened above, the developer PC must hear that this command is over,
            // or its CLI would wait forever and history would show it as still running.
            record.ExitCode = code;
            record.EndUtc = DateTime.UtcNow;
            if (!background)
            {
                if (!_alive)
                {
                    record.Orphaned = true;
                    runtime.Audit.Write($"#{record.No} finished while disconnected: exit {code}");
                }
                else if (!tool.Quiet && code != 0)
                {
                    runtime.Audit.Write($"#{record.No} exit {code}");
                }
            }

            await TrySendAsync(new Msg { Type = "exit", Id = m.Id, Code = code });
        }
    }

    private async Task<bool> TrySendAsync(Msg msg)
    {
        if (!_alive) return false;
        try
        {
            await channel.SendAsync(msg);
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or System.Net.Sockets.SocketException)
        {
            _alive = false;
            return false;
        }
        catch (ArgumentException)
        {
            return false; // a message too large for one frame: dropped, the session itself is fine
        }
    }

    /// <summary>
    /// Sends a file to the developer PC. A temporary file (a dump, a trace, a zipped folder) is deleted only once it
    /// has actually been delivered: if the link is down it is kept in the session workspace and the command's output
    /// says where, so it can be pulled after reconnecting.
    /// </summary>
    private async Task SendFileAsync(int id, string name, string path, bool zip, bool deleteAfter, OutputSink sink)
    {
        var delivered = false;
        try
        {
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, true);
            if (await TrySendAsync(new Msg { Type = "blob", Id = id, Name = name, Size = fs.Length, Zip = zip }))
            {
                var buf = new byte[256 * 1024];
                int n;
                while ((n = await fs.ReadAsync(buf)) > 0) await channel.SendDataAsync(id, buf.AsSpan(0, n), false);
                await channel.SendDataAsync(id, [], true);
                delivered = true;
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or System.Net.Sockets.SocketException)
        {
            _alive = false;
        }

        if (!deleteAfter) return;
        try
        {
            if (delivered)
            {
                File.Delete(path);
                return;
            }

            var keep = Path.Combine(runtime.WorkDir, "undelivered");
            Directory.CreateDirectory(keep);
            var kept = Path.Combine(keep, zip ? name + ".zip" : name);
            File.Move(path, kept, overwrite: true);
            sink.Write($"[the connection dropped before {name} was delivered. It is kept on the target: agentprobe pull \"{kept}\"]\n", true);
            runtime.Audit.Write($"undelivered file kept: {kept}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort: the file stays where the tool wrote it
        }
    }

    private void BeginUpload(Msg m)
    {
        try
        {
            var dest = runtime.ResolvePath(m.Path ?? "");
            var upload = new Upload(dest, m.Zip, m.Name);
            _uploads[m.Id] = upload;
            runtime.Audit.Write($"push {Format.Bytes(m.Size)} -> {dest}");
            activity("push " + Format.Clip(dest, 60));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _uploads[m.Id] = Upload.Failed(ex.Message);
        }
    }

    private async Task HandleDataAsync(Frame frame)
    {
        if (!_uploads.TryGetValue(frame.DataId, out var upload)) return;
        upload.Write(frame.Data!);
        if (!frame.Eof) return;

        _uploads.TryRemove(frame.DataId, out _);
        var (ok, text) = upload.Finish();
        await TrySendAsync(new Msg { Type = "out", Id = frame.DataId, Err = !ok, Text = text + "\n" });
        await TrySendAsync(new Msg { Type = "exit", Id = frame.DataId, Code = ok ? 0 : 1 });
    }

    /// <summary>Receives a pushed file (or a zipped folder) into a temp file, then moves/extracts it into place.</summary>
    private sealed class Upload
    {
        private readonly string _dest = "";
        private readonly bool _zip;
        private readonly string? _fileName;
        private readonly string _temp = "";
        private FileStream? _stream;
        private string? _error;
        private long _bytes;

        public Upload(string dest, bool zip, string? fileName)
        {
            _dest = dest;
            _zip = zip;
            _fileName = fileName;
            _temp = Path.Combine(Path.GetTempPath(), $"{AppInfo.Name}-up-{Guid.NewGuid():N}.tmp");
            _stream = new FileStream(_temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        }

        private Upload(string error) => _error = error;

        public static Upload Failed(string error) => new(error);

        public void Write(byte[] data)
        {
            if (_error != null || _stream == null) return;
            try
            {
                _stream.Write(data);
                _bytes += data.Length;
            }
            catch (IOException ex)
            {
                _error = ex.Message;
            }
        }

        public (bool ok, string text) Finish()
        {
            _stream?.Dispose();
            _stream = null;
            if (_error != null)
            {
                Abort();
                return (false, "push failed: " + _error);
            }

            try
            {
                if (_zip)
                {
                    Directory.CreateDirectory(_dest);
                    ZipFile.ExtractToDirectory(_temp, _dest, overwriteFiles: true);
                    var count = Directory.EnumerateFiles(_dest, "*", SearchOption.AllDirectories).Count();
                    return (true, $"pushed folder -> {_dest} ({count} files, {Format.Bytes(_bytes)} zipped)");
                }

                // Pushing a file onto an existing folder (or a path ending in a slash) drops it inside.
                var dest = _dest;
                if (Directory.Exists(dest) && _fileName != null) dest = Path.Combine(dest, _fileName);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Move(_temp, dest, overwrite: true);
                return (true, $"pushed -> {dest} ({Format.Bytes(_bytes)})");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return (false, "push failed: " + ex.Message);
            }
            finally
            {
                Abort();
            }
        }

        public void Abort()
        {
            _stream?.Dispose();
            _stream = null;
            try
            {
                if (_temp.Length > 0 && File.Exists(_temp)) File.Delete(_temp);
            }
            catch (IOException)
            {
                // best effort
            }
        }
    }

    /// <summary>Batches a tool's output into few messages, keeps a copy in the run record, and survives a dead connection.</summary>
    private sealed class OutputSink
    {
        private readonly TargetSession _session;
        private readonly int _id;
        private readonly RunRecord _record;
        private readonly Channel<(bool err, string text)> _queue = Channel.CreateUnbounded<(bool, string)>(new UnboundedChannelOptions { SingleReader = true });
        private readonly Task _pump;

        public OutputSink(TargetSession session, int id, RunRecord record)
        {
            _session = session;
            _id = id;
            _record = record;
            _pump = Task.Run(PumpAsync);
        }

        /// <summary>
        /// Largest piece of text put into one message. JSON may expand a character to six bytes (\uXXXX), so this
        /// keeps every message far below the 4 MiB frame limit no matter what a tool writes in one go.
        /// </summary>
        private const int MaxChunk = 100_000;

        public void Write(string text, bool err)
        {
            _record.Append(text);
            for (var i = 0; i < text.Length; i += MaxChunk)
            {
                var length = Math.Min(MaxChunk, text.Length - i);

                // Do not cut a surrogate pair in half.
                if (i + length < text.Length && char.IsHighSurrogate(text[i + length - 1])) length--;
                _queue.Writer.TryWrite((err, text.Substring(i, length)));
                if (length < MaxChunk && i + length < text.Length) i -= MaxChunk - length;
            }
        }

        public async Task CompleteAsync()
        {
            _queue.Writer.TryComplete();
            try
            {
                await _pump;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // a failed send must not keep the command from reporting its exit code
            }
        }

        private async Task PumpAsync()
        {
            var sb = new System.Text.StringBuilder();
            while (await _queue.Reader.WaitToReadAsync())
            {
                await Task.Delay(15); // let a burst of lines collect into one message
                var currentErr = false;
                while (_queue.Reader.TryRead(out var item))
                {
                    if (sb.Length > 0 && (item.err != currentErr || sb.Length + item.text.Length > MaxChunk))
                    {
                        await _session.TrySendAsync(new Msg { Type = "out", Id = _id, Err = currentErr, Text = sb.ToString() });
                        sb.Clear();
                    }

                    currentErr = item.err;
                    sb.Append(item.text);
                }

                if (sb.Length > 0)
                {
                    await _session.TrySendAsync(new Msg { Type = "out", Id = _id, Err = currentErr, Text = sb.ToString() });
                    sb.Clear();
                }
            }
        }
    }
}
