<p align="center"><img src="docs/icon.png" width="96" alt="agentprobe"></p>
<h1 align="center">agentprobe</h1>
<p align="center">
  Let your AI coding agent debug and test <b>another Windows PC</b> on your network.<br>
  One exe. No install, no SSH, no accounts, no firewall changes on the PC being tested.
</p>
<p align="center"><b>English</b> · <a href="README.ko.md">한국어</a></p>

---

"It only happens on that PC." The usual answer is to install an SSH server there, open a firewall port, hand over
credentials, and remember to undo all of it afterwards. **agentprobe** replaces that with one step:

1. Copy `agentprobe.exe` to the PC with the problem, double-click it and choose **Share this PC**.
2. It finds your developer PC on the network by itself. The person at that PC presses **Enter** to allow it.
3. Your agent (Claude Code, or you in a terminal) runs `agentprobe ...` commands on your PC. They execute over there.

Close the window on the tested PC and it is over. Nothing was installed.

> **If you are an AI agent and a user pointed you at this page:** you can use the tool right away.
> 1. If `agentprobe` is not available on this PC, download `agentprobe.exe` from this repository's latest release
>    (one file, Windows x64, no installer) and run it by its path.
> 2. Run `agentprobe guide` and follow it. `agentprobe list` shows the PCs you can work on.
> 3. If no PC is listed, ask the user to copy the same exe to the PC with the problem, run it there and approve.

```
 DEVELOPER PC                                       PC BEING TESTED
 agent ──▶ agentprobe exec / ps / watch / ...       agentprobe.exe  (double-clicked)
                 │                                         │
           background daemon  ◀──── encrypted TCP ─────────┘   the tested PC dials OUT,
           (starts by itself)                                   so it needs no open port
```

## Quick start

**On your developer PC** (once):

Put `agentprobe.exe` in a folder where it can stay, open a terminal there and run:

```powershell
.\agentprobe.exe setup
```

This adds that folder to your user `PATH`, so you and your agent can type just `agentprobe` from any new terminal, and
opens TCP/UDP 48650 in Windows Firewall so tested PCs can find this one (it asks for administrator rights once).
`agentprobe setup --remove` undoes both.

**On the PC to test:** copy `agentprobe.exe` there and run it. It lists the developer PCs it found:

```
  [1] DEV-PC  (user alex)  192.168.0.20  key 3F9A-21C4-88D0-B7E1

Let DEV-PC run commands on this PC with the rights of tester? [Y/n]
```

**Back on your developer PC:**

```powershell
agentprobe list                         # who is connected
agentprobe triage                       # what is wrong with that PC right now
agentprobe exec 'Get-Service Spooler'   # run anything (PowerShell by default)
agentprobe guide                        # the playbook an AI agent should read first
```

It is a plain command-line tool, so **any agent that can run a shell command can use it**: Claude Code, Codex,
Gemini CLI, Cursor and others. There is nothing to register. Add one line to the instructions file your agent reads
(`CLAUDE.md`, `AGENTS.md`, ...):

```markdown
To debug or test on the remote Windows PC, use the `agentprobe` CLI. Run `agentprobe guide` first.
```

There is one guide for every agent, and it is built into the exe: `agentprobe guide` prints it, and the dashboard's
**Connect an agent** button shows it together with the line to copy. People use the same commands in a terminal, or
the dashboard below.

### MCP, if your agent is set up that way

The same exe is also an MCP server (stdio):

```powershell
claude mcp add agentprobe -- "C:\tools\agentprobe.exe" mcp      # Claude Code
codex mcp add agentprobe -- "C:\tools\agentprobe.exe" mcp       # Codex
```

For other clients, register the command `agentprobe.exe` with the single argument `mcp`.

The server is deliberately thin, because MCP tool definitions occupy an agent's context on every turn. It offers
four tools, `list_targets`, `run` (any agentprobe command line), `screenshot` (returned as an image) and `guide`,
about 2 KB in total, and its instructions tell the agent where the exe is, so an agent with a shell moves on to the
command line, where output can be piped, filtered and saved to files. Nothing is lost through MCP: `run` reaches
every command in this README.

The tested PC looks for the developer PC on every network it is on: by broadcast, and by a probe to each address of
every subnet up to /22 (1022 addresses), which is what finds it across a VPN, since VPN tunnels carry neither
broadcast nor multicast. `agentprobe share --dry-run` lists where it looks and which developer PCs answer, without sharing anything. A developer PC
behind a router or on a larger VPN is not found that way; start it with the address instead:
`agentprobe share 192.168.0.20`.

## Why not just SSH?

| | SSH | agentprobe |
|---|---|---|
| Setup on the tested PC | install OpenSSH server, open port 22, create or share an account | copy one exe, run it |
| Cleanup afterwards | uninstall, close the port, remove keys | close the window |
| Finding the PC | you need its address | automatic on the same network |
| GUI apps | invisible (session 0) | run in the user's desktop session: visible, screenshot-able, clickable |
| Cost for an AI agent | raw shell output: long, padded, often thousands of tokens per look | purpose-built commands that answer in a few compact lines |
| Monitoring | poll in a loop | `watch` records in the background and reports trends |
| A dropped connection | the command dies with the session | the command keeps running; `history` and `result` pick it up |
| The person at the PC | sees nothing | on-screen banner, live activity list, log file |

## Built for agents: fewer tokens, fewer round trips

- **Compact answers.** `agentprobe proc MyApp` is ten lines: path, command line, user, bitness, memory and peaks,
  handles, GDI/USER objects, windows, children. The equivalent PowerShell is a paragraph of code and a page of output.
- **Filtering happens on the tested PC.** `--grep`, `--tail`, `--since`, `--top` send back only what matters.
- **Incremental reads.** `logs`, `dbg` and `read --new` return only what is new since the previous call.
- **Trends instead of samples.** `watch` answers "private bytes 210M→480M, +22M/min, steady growth: LIKELY LEAK"
  in one line, after one call.
- **Blocking waits.** `wait --window "Setup" --timeout 60s` replaces a polling loop.
- **Output is capped.** Long output prints its first 200 and last 100 lines and is saved to a local file to search.
- **Fleets collapse.** Run on fifty PCs with `-t all`; identical results are printed once with the list of PCs.

## What it can do

| Area | Commands |
|---|---|
| Run | `exec` (PowerShell, pwsh, 32-bit PowerShell, cmd; `exec -f script.ps1 -Arg value` sends a script with its arguments), `start` / `jobs` / `logs` / `kill` background jobs (`start --exe app.exe` tracks a GUI app itself), `wait` for a process, window, port, file, log line or idle CPU |
| Files | `push`, `pull` (files and folders), `ls`, `read` (tail, grep, line ranges, only-new), `stat` (version, x86/x64, .NET or native, SHA-256, blocked-download flag), `locks` (who holds a file open) |
| First look | `triage`: load, hung windows, recent crashes and errors, low disk, pending reboot, and what to try next |
| Processes | `ps`, `top`, `proc`, `threads` (CPU per thread, wait reason, start module), `stacks` (call stacks of a live process, identical ones grouped), `modules` (loaded DLLs, versions, paths), `handles` (by type, and what grew), `vmmap` (which kind of memory grows: heaps, stacks, images, mapped files), `dump` (small or full memory dump, fetched to your PC) |
| System | `sys` (OS build, hardware, locale, GPU drivers, displays, .NET / VC++ runtimes), `mem` (commit, pools, standby lists, biggest consumers), `net` (listeners, connections per process, reachability checks), `svc`, `reg` |
| Why only there? | `compare <pc> local` (differences in OS, drivers, runtimes, environment, PATH, installed programs, services, files), `envsnap --save/--diff` (what an installer or update changed), `deps` (missing DLLs, x86/x64 mismatch, missing exports, missing .NET runtime) |
| Logs | `events` (Windows event logs, filtered and de-duplicated), `crashes` (faulting module, exception code, dump files, blue screens), `dbg` (OutputDebugString capture, like DebugView) |
| Monitor | `watch`: per-process private bytes, handles, GDI/USER objects, threads and CPU with growth rates and a leak verdict; process start/exit with decoded exit codes; windows that stop responding |
| Capture | `trace` (which file and registry accesses FAIL for a process, counted and grouped: Process Monitor's most useful view as a short table), `crashdumps on` (Windows saves a dump whenever the app crashes), `wpr start/stop` (Windows Performance Recorder traces: CPU, disk, file, registry, heap) |
| Desktop | `ui` (a window's controls as text; click, read and fill them by name), `windows` (also flags a Windows prompt waiting for the person: firewall, UAC, credentials), `screenshot` (screen, display or one window, scaled), `click`, `drag`, `scroll`, `type`, `key`, `focus` |
| Session | `list`, `dashboard`, `name`, `history`, `result`, `reboot --wait`, `end` |

`agentprobe help` lists everything; `agentprobe help <command>` shows the options.

### A leak hunt, start to finish

```powershell
agentprobe push .\bin\Release build            # your build lands in the session workspace
agentprobe watch start MyApp
agentprobe start --name app '.\build\MyApp.exe'
agentprobe exec -f .\stress-scenario.ps1
agentprobe watch
```
```
watching MyApp, every 2s for 12m03s (361 samples)
system   cpu avg 12% max 97%   ram 57%->61%   commit 70%->74%
MyApp.exe(5120)  private 210M->480M (+22.4M/min, steady growth: LIKELY LEAK)  handles 812->4310 (+291/min, steady growth: LIKELY LEAK)  gdi 120  user 85  threads 24->25
events since the last report (2):
  14:01:03 start helper.exe(4321) by MyApp.exe(5120)
  14:01:05 exit  helper.exe(4321) code 0xC0000005 access violation after 2.1s
```
```powershell
agentprobe handles MyApp --interval 30s        # which handle type is growing
agentprobe dump MyApp --full -o .\dumps\       # open in WinDbg / Visual Studio / dotnet-dump
```

## Many PCs

- `agentprobe name -t LAB-PC07 gpu-rig` gives a PC a short name. It is remembered on your developer PC.
  A PC can also name itself: `agentprobe share --name gpu-rig`.
- `-t gpu-rig`, `-t a,b,c`, `-t "LAB-*"`, `-t all`. With several targets, identical results are grouped:
  ```
  == 47 targets (exit 0): LAB-PC01, LAB-PC02, ... +35 more
  10.0.26200.1742
  == 3 targets (exit 0): LAB-PC11, LAB-PC12, LAB-PC30
  10.0.19045.5011
  ```
- `agentprobe dashboard` opens a live web page for people:
  - **Screens**: a wall of live screen thumbnails, one tile per PC, with state, CPU and RAM. Tile size and refresh
    pace (5 s to 1 min, or paused) are adjustable. Only tiles that are on screen are refreshed, and each refresh is
    one small capture, so watching a hundred PCs does not slow them down.
  - **List**: a sortable, searchable table for fleets too large to eyeball.
  - **Detail panel** per PC: health summary, system facts, a larger live view of the screen, processes, the history of
    commands run there, and a **console** that runs any agentprobe command on that PC.
  - Light and dark theme, keyboard shortcuts (`/` search, `g` screens, `l` list, `t` theme, `Esc` close).

  The page is served only on `127.0.0.1` and guarded by a random token. Viewing a PC's screen from the dashboard is
  noted in that PC's activity log.
- `agentprobe list --all` includes PCs that went offline, with when they were last seen and what they were doing.

Double-clicking the exe asks which side this PC is: `1` shares it for testing, `2` makes it the developer PC and shows
the start screen. Enter takes what the PC was last time (a PC that has been a developer PC stays one; any other PC
is shared). `agentprobe share` and `agentprobe home` go there without the question.

The start screen is a short list of lines to click. Four of them copy what an agent needs: the one line for its
instructions file, the `claude mcp add` / `codex mcp add` commands with this exe's path already filled in, and a
prompt to paste into a chat. The others open the dashboard, list the connected PCs, share this PC, run the one-time
setup and, when there is one, install an update. Click a line or press its number (lines highlight under the mouse);
the PCs that are connected are listed live. A command line stays at the bottom of the window: type `list`, `help` or `-t <name> triage` there instead
of opening another terminal. It adapts to the window size and works in Windows Terminal and in the classic console,
from cmd and from PowerShell. Piped (an agent calling `agentprobe home`), the same information comes as plain text.
Typing `share` there opens the sharing window for this PC.

Closing a start screen that was opened by a double-click also ends agentprobe's background process, unless a PC is
connected, another agentprobe command is in progress, or the dashboard page asked for something within the last 90
seconds; the next `agentprobe` command starts it again. From a terminal,
`agentprobe stop --if-idle` does the same and `agentprobe stop` always ends it.

## Updates

```powershell
agentprobe update --check        # is there a newer release?
agentprobe update                # install it on this PC
agentprobe -t LAB-PC01 update    # bring a connected PC to this PC's version
```

agentprobe is one exe, and updating it keeps it that way. The background process on the developer PC looks at this
repository's latest release twice a day (set `AGENTPROBE_NO_UPDATE_CHECK=1` to stop that; a tested PC never looks). When
there is a newer one, the start screen shows a line to click, the dashboard a button, and `agentprobe list` says so
in one line. Installing downloads `agentprobe.exe` and its `.sha256` from the release, refuses the file unless the
checksum matches and the exe starts, moves the running exe aside (a running file cannot be overwritten, but it can be
renamed), puts the new one in its place and restarts the background process from it. What was moved aside is deleted
once nothing runs from it any more (at the latest when agentprobe is started the next time), so no second file stays
behind. An open dashboard page reloads itself as the new
version.

Connected PCs keep their sessions through this: they reconnect by themselves within seconds and commands running
there keep running. Only a command that is being waited for on the developer PC ends with "connection lost" and its
run number (`agentprobe result <n>` fetches the result), so while commands are running the update explains this,
ends with exit code 4 and waits for `--force`, or a second click.

A tested PC is updated from the developer PC, not from the internet: `agentprobe -t <name> update` sends this PC's
exe over the existing connection, and agentprobe restarts there from it in a new window, with the session, its
approval and its workspace carried over. Background jobs and commands that are running there are not carried over,
so that too asks for `--force` while there are any. A tested PC that still runs 0.1 has to get the new exe by hand
once.

The checksum comes from the same release as the exe: it guards against a broken download, not against a release
that was tampered with. Use `AGENTPROBE_UPDATE_URL` to point at a mirror of your own (https only) if you distribute
builds inside your organisation.

## Reboots

```powershell
agentprobe reboot --wait --timeout 10m
```

The tested PC registers a **one-shot** autostart, reboots, and reconnects when the same user logs on again, without
asking a second time. No service is installed: a service would run in session 0 as SYSTEM, where GUI apps are
invisible and the user's profile is not loaded, which is the wrong place to reproduce a user's problem. The autostart
entry removes itself. Unattended PCs need Windows auto-logon. Background jobs do not survive the reboot; the session
workspace, the approval (for 24 hours) and the command history do, so `agentprobe history` still shows what ran before
the reboot and which command the reboot cut off.

## Safety

agentprobe gives a remote machine command execution on a PC, so the design assumes it will be pointed at with suspicion:

- **Consent is local and explicit.** Someone at the tested PC must start the exe and approve one specific developer PC.
  The prompt says whose rights are being handed over (and says so loudly when it is administrator rights).
- **It is always visible.** While a developer PC is connected, a banner sits in the top-left corner of the screen:
  *Remote control active*. It cannot be switched off, locally or remotely. It is excluded from screen capture and
  click-through, so it does not disturb the app under test. The console window lists each command as it runs.
- **There is a record.** Every command, file transfer, screenshot and input action is appended to
  `agentprobe-activity.log` next to the exe. There is no remote command to edit or delete that file; `end --remove-exe`
  leaves it behind on purpose.
- **Keys, not passwords.** Each developer PC has a signing key (like an SSH host key). The tested PC pins the key it
  approved; another machine cannot take over the session or pose as that developer PC later. Traffic is encrypted
  with per-connection keys (ECDH P-256, AES-256-GCM).
- **Nothing listens on the tested PC.** It only makes an outbound connection.
- **One developer PC at a time.** A tested PC is connected to exactly one developer PC, and only one agentprobe
  shares a PC at a time: a second copy refuses to start.
- **It cleans up.** `agentprobe end`, or closing the window, stops what was started, deletes the workspace and reverts
  the settings agentprobe itself changed.

What it does not protect against: the approved developer PC itself. Whoever controls that PC (and its agent) can do
anything the approving user could do on the tested PC. Approve only machines you trust, on networks you trust.
For unattended labs, `agentprobe share --trust 3F9A-21C4-88D0-B7E1` accepts exactly one developer key without a
prompt; `--yes` accepts the first one that answers and is only for isolated networks.

Unsigned executables that accept remote commands look like malware to antivirus heuristics. If you distribute a build
inside your organisation, sign it.

## 32-bit and 64-bit

`agentprobe.exe` is a 64-bit program and needs 64-bit Windows 10 or 11. It inspects 32-bit and 64-bit processes alike.
For 32-bit apps: `deps` searches `SysWOW64` and reports x86/x64 mismatches, `exec --shell ps32` and `reg --view 32`
show what a 32-bit process sees, and `dump` notes the extra step WinDbg needs for a 32-bit process dumped by a 64-bit tool.

## Requirements and limits

- Windows 10 (1809 or later) or Windows 11, x64, on both PCs. No .NET runtime needed.
- Both PCs on the same network for automatic discovery; otherwise give the address. There is no relay server yet,
  so the tested PC must be able to reach the developer PC directly (LAN or VPN).
- Commands run with the rights of the user who started agentprobe on the tested PC. A few tools need it started with
  *Run as administrator*: `wpr`, `crashdumps on`, memory page lists, other users' processes.
- The secure desktop (UAC prompts, the lock screen) cannot be captured or clicked.
- Language: the dashboard and everything a person reads on the tested PC (approval prompt, banner) come in English
  and Korean and follow the Windows display language; switch in the dashboard, or start with `--lang en|ko`.
  What a tool reports from the tested PC and the activity log are always English, so agents read the same text on
  every machine. What the command line says itself (`list`, `help`, guidance and errors) is Korean for a person at a
  console on a Korean Windows and English when the output is piped, as it is for an agent; an agent whose shell is
  a pseudo-terminal can set `AGENTPROBE_LANG=en`.
- Developed and tested on Windows 11. Version 0.x: the protocol may still change, so use the same version on both PCs
  (`agentprobe list` says when a connected PC runs another one, and `agentprobe -t <name> update` fixes that).

## Does it tell the truth?

A debugging tool that reports wrong numbers is worse than none, so the repository carries a verification script that
checks agentprobe against Windows itself and against scenarios with a known answer:

```powershell
.\tests\verify.ps1 -Session            # about 4 minutes; add -SkipInput on a PC someone is using
```

It compares process memory, handle and thread counts, system memory, ports and file hashes with what Windows reports;
searches a 70 MB log for a line at its very end; starts a process that leaks on purpose and checks that the leak is
flagged with the right rate, and only after two minutes of evidence; drives a test window through UI Automation and
real keyboard and mouse input; kills a process and checks its children survive; round-trips files and folders; cuts a
connection and recovers the result. Run it after any change, on the PCs you care about.

Some answers are inherently approximate and say so: function names in `stacks` come from export tables unless a PDB
is present (internal functions are shown as module+offset), a leak verdict is a statistical judgement, and `trace`
reports when the system dropped events under load.

## Build

```powershell
dotnet build                                            # .NET 10 SDK
dotnet publish src/AgentProbe -c Release -o publish     # one native exe (needs the VS "Desktop development with C++" workload)
```

`tools/make-icon.py` regenerates the icon.

Releases are built by GitHub Actions. Pushing a tag (`git tag v0.2.0; git push origin v0.2.0`), or running the
`build` workflow by hand with a version, builds the exe with that version, runs the verification script, and
publishes a release with `agentprobe.exe` and its SHA-256 checksum.

## License

[MIT](LICENSE). The dashboard embeds the [Pretendard](https://github.com/orioncactus/pretendard) typeface, which is
licensed under the SIL Open Font License 1.1 (`src/AgentProbe/Controller/fonts/OFL.txt`).
