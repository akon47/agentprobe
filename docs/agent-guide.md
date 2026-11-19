# agentprobe: guide for AI agents

You are on the developer PC. `agentprobe` lets you run commands and diagnostics on another Windows PC (the *target*)
that a person has shared with this PC. Everything you do is shown on the target's screen and written to a log there.

If `agentprobe` is not found, it is not on PATH yet: call the exe by its full path, or ask the user to run
`agentprobe.exe setup` once and open a new terminal.

## Rules of thumb

1. **Look before you run.** `agentprobe list` shows connected targets. With one target you can omit `-t`.
2. **Start with `agentprobe triage`** (add the app's name: `triage MyApp`). It tells you which tool to use next.
3. **Prefer a built-in tool over `exec`.** Built-ins answer in a few compact lines; a PowerShell pipeline that does the
   same costs several times more tokens and is easy to get subtly wrong.
4. **Filter on the target.** Use `--grep`, `--tail`, `--since`, `--top`, `--new` so only the relevant lines come back.
5. **Do not poll.** Use `wait` to block until something happens, and `watch` to record trends while a test runs.
6. **Long output is cut for you** (first 200 + last 100 lines) and saved to a local file whose path is printed.
   Search that file locally instead of re-running with `--full`.
7. **After a dropped connection or a new session, run `agentprobe history`** to see how far the work got.
   `orphaned` runs finished after the connection dropped; `agentprobe result <#>` returns their output.
8. Leave the target as you found it. Things agentprobe itself changed (jobs, workspace, crash-dump settings,
   a running trace) are undone by `agentprobe end`; things you changed with `exec` are yours to undo.

## Which tool for which question

| Question | Command |
|---|---|
| What is wrong right now? | `triage [app]` |
| What machine is this (OS build, runtimes, locale, GPU driver)? | `sys` |
| Why does it work on my PC but not there? | `compare <target> local` (add `--dir <app folder>`) |
| Why does the app not start? | `deps <exe>`, then `stat <exe>`, then `crashes`, `events` |
| Is it running? How big? | `ps <name>`, `proc <name>` |
| What is using the CPU? Which thread? | `top`, `threads <pid>` |
| Is it frozen? Where is it stuck? | `windows` (NOT RESPONDING), `threads <pid>`, `stacks <pid>`, then `dump <pid>` |
| Is it leaking memory / handles / GDI objects? | `watch start <name>` ... run scenario for 2+ minutes ... `watch`; `handles <pid> --interval 30s`; `vmmap <pid> --interval 30s` (which kind of memory) |
| Which file or registry access fails? (missing config, DLL, key; access denied) | `trace start <name>` ... reproduce ... `trace` (needs an elevated target) |
| Did it crash? Why? | `crashes`, `events --level error`, `watch` (exit codes), `crashdumps on` before reproducing |
| What does the app log? | `read <file> --new` / `--grep`, `dbg` (OutputDebugString), `logs <job>` |
| Which DLL version did it load, from where? | `modules <pid> --grep <name>` |
| Who holds this file open? | `locks <file>` |
| Can it reach the server? Who listens on the port? | `net check host:port`, `net --listen`, `net --name <app>` |
| What did the installer / update change? | `envsnap --save before` ... `envsnap --diff before` (`--dir`, `--reg`) |
| What is in this window? Press a button, fill a field | `ui <window>` (controls as text), `ui click <name>`, `ui set <name> <text>`, `ui get <name>` |
| What does the screen show? | `windows` first (cheap), `ui` for the controls; `screenshot --window <title> --scale 0.5` when you need pixels |
| Where is the time going (deep)? | `wpr start cpu file` ... `wpr stop` (needs an elevated target) |

## Running things

```
agentprobe exec 'Get-ChildItem C:\App | Select-Object -First 5'     # PowerShell; exit code is the remote exit code
agentprobe exec --shell cmd 'dir /b C:\App'
agentprobe exec --shell ps32 '...'                                   # 32-bit PowerShell (what a 32-bit app sees)
agentprobe exec -f .\repro.ps1 --timeout 10m                         # send a local script and run it
agentprobe start --name app 'C:\App\MyApp.exe --verbose'             # background job; survives disconnects
agentprobe logs app            # only output that is new since the last call
agentprobe wait --window "MyApp" --timeout 30s
agentprobe kill app
```

- Quote the command with single quotes so your local shell leaves `$` and `|` alone.
- stdin is closed: anything that prompts fails immediately instead of hanging. Pass `-Force`/`-Confirm:$false`/`/quiet`.
- Commands run as the logged-on user **in their desktop session**: a GUI app you start is visible there.
- The default folder is the session workspace. `agentprobe push .\bin\Release build` puts a folder there;
  `agentprobe exec '.\build\MyApp.exe'` then runs it. The workspace is deleted at `end`.
- Rights are those of whoever started agentprobe on the target (`list` shows `user` or `admin`). You cannot elevate
  remotely. If a tool says it needs an elevated target, tell the user to restart agentprobe there with
  *Run as administrator*.

## Typical sessions

Leak hunt:
```
agentprobe watch start MyApp --interval 2s
agentprobe exec -f .\run-scenario.ps1
agentprobe watch                 # private bytes / handles / GDI / USER with growth per minute and a leak verdict
agentprobe handles MyApp --interval 30s    # which handle type grows
agentprobe dump MyApp --full -o .\dumps\   # for heap analysis on this PC
```

Crash on start:
```
agentprobe deps 'C:\App\MyApp.exe'        # missing DLL / x86-x64 mismatch / missing .NET runtime
agentprobe crashdumps on MyApp.exe        # (elevated target) capture a dump at the moment of the crash
agentprobe exec 'C:\App\MyApp.exe'; agentprobe crashes MyApp
agentprobe pull 'C:\Users\...\CrashDumps\MyApp.exe.1234.dmp' .\dumps\
```

Reboot in the middle of a test:
```
agentprobe reboot --wait --timeout 10m    # returns when the target has logged on again and reconnected
agentprobe history                        # confirm where you were
```
Unattended targets need Windows auto-logon; background jobs do not survive a reboot, the workspace does.

Driving the UI: prefer names over pixels. `ui "<window title>"` lists the controls; `ui click "Save"`,
`ui set "#fileName" "report.txt"` and `ui get "#status"` act on them, and keep working when the layout or DPI changes.
Fall back to `screenshot` → `click <x> <y>` (coordinates of that image; `--hover` only moves the pointer, for
tooltips and hover states), `type <text>` (typed literally; use
`key enter` / `key tab` for those keys), `key ctrl+s`, `focus <title>` when a control has no name. Prefer `windows`
and `wait --window` over screenshots when you only need to know that something appeared.

How far to trust an answer: counters (`ps`, `mem`, `handles`) are what Windows reports. `watch` calls a leak only
after two minutes of steady growth and says "too early" before that. `stacks` names internal functions as
module+offset unless a PDB is present, and cannot walk managed/JIT frames. `trace` says so if events were dropped.
`kill --pid` ends that one process; add `--tree` to include what it started.

## Several targets

`-t <name>` picks one, `-t a,b` or `-t "LAB-*"` or `-t all` runs on many at once: identical results are printed once
with the list of targets that produced them. `agentprobe name -t LAB-PC07 gpu-rig` gives a target a short name.
`agentprobe list --all` also shows targets that went offline, with their last command.
`-t local` runs a tool on the developer PC itself.

## Exit codes

The remote command's own exit code for `exec`; `0` success; `2` usage; `3` no such target; `75` connection lost
(the command may still be running: see `history`); `124` timeout; `130` cancelled. Crash codes are decoded, e.g.
`0xC0000005 access violation`, `0xC0000135 DLL not found`, `0xC000007B x86/x64 mismatch`, `0xE0434352 .NET exception`.

Full option lists: `agentprobe help <command>`.
