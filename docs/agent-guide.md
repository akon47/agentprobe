# agentprobe: guide for AI agents

You are on the developer PC. `agentprobe` lets you run commands and diagnostics on another Windows PC (the *target*)
that a person has shared with this PC. Everything you do is shown on the target's screen and written to a log there.

If `agentprobe` is not found, it is not on PATH yet: call the exe by its full path, or ask the user to run
`agentprobe.exe setup` once and open a new terminal.

## Rules of thumb

1. **Look before you run.** `agentprobe list` shows connected targets. With one target you can omit `-t`, but for
   work of more than a few commands name it from the start (`-t <name>`, or set `AGENTPROBE_TARGET=<name>`): another
   PC may connect while you work. When that happens, commands without `-t` keep going to the PC you were using and
   say so in a `[...]` line (for half an hour after the last one; `end` and `reboot` always need the name then).
   When the PC you were using drops and another is connected, an unnamed command is refused rather than run there.
   `list` shows when each PC connected.
2. **Start with `agentprobe triage`** (add the app's name: `triage MyApp`). It tells you which tool to use next.
3. **Prefer a built-in tool over `exec`.** Built-ins answer in a few compact lines; a PowerShell pipeline that does the
   same costs several times more tokens and is easy to get subtly wrong.
4. **Filter on the target.** Use `--grep`, `--tail`, `--since`, `--top`, `--new` so only the relevant lines come back.
5. **Do not poll.** Use `wait` to block until something happens, and `watch` to record trends while a test runs.
6. **Long output is cut for you** (first 200 + last 100 lines) and saved to a local file whose path is printed.
   Search that file locally instead of re-running with `--full`.
7. **After a dropped connection or a new session, run `agentprobe history`** to see how far the work got.
   `orphaned` runs finished after the connection dropped; `agentprobe result <#>` returns their output. History
   also survives a reboot of the target and an update of agentprobe there (those entries have no output any more).
8. Leave the target as you found it. Things agentprobe itself changed (jobs, workspace, crash-dump settings,
   a running trace) are undone by `agentprobe end`; things you changed with `exec` are yours to undo.
9. **`paused by the person at the target` (exit 5) means stop.** Someone at that PC pressed the space bar in agentprobe's window (Enter there resumes)
   because they need the PC. Nothing was run. Do not retry in a loop: tell the user, and go on when they say so.

## Which tool for which question

| Question | Command |
|---|---|
| What is wrong right now? | `triage [app]` |
| What machine is this (OS build, runtimes, locale, GPU driver)? | `sys` |
| Why does it work on my PC but not there? | `compare <target> local` (add `--dir <app folder>`) |
| Why does the app not start? | `triage <app>` (finds where it is installed: `found` for a program of that name, `maybe` for one whose name only contains it), `start --exe <path>` or `start --lnk <shortcut>` (the exit code, named), `deps <exe>` (DLLs, and for a .NET app what its host needs), `deps <exe> --run` (.NET: what the host does with it), then `stat <exe>`, `crashes`, `events` |
| Is it running? How big? | `ps <name>`, `proc <name>` |
| What is using the CPU? Which thread? | `top`, `threads <pid>` |
| Is that worker thread there? Since when? | `threads <pid> --grep <name>` (NAME and CREATED of each thread), `threads <pid> --by-name` (how many per name) |
| Is it frozen? Where is it stuck? | `windows` (NOT RESPONDING), `threads <pid>`, `stacks <pid>`, then `dump <pid>` |
| Is it leaking memory / handles / GDI objects? | `watch start <name>` ... run scenario for 2+ minutes ... `watch`; `handles <pid> --interval 30s`; `vmmap <pid> --interval 30s` (which kind of memory) |
| .NET app: how many objects of a type are alive, and what holds them? | `heap <name> --windows` (window objects per type: how many are SHOWN and how many are CLOSED but still referenced, the usual window leak), `heap <name> --live --type <Type>` (count before and after a fix; several at once: `--type "A,B,C"`), `heap <name> --why <Type>` (the chain of references from its root), `heap <name> --show <Type> --fields a,b` (the values in its fields: is it disposed, which device is it). Read on the target from a snapshot: no dump to carry |
| Which file or registry access fails? (missing config, DLL, key; access denied) | `trace start <name>` ... reproduce ... `trace` (needs an elevated target) |
| Did it crash? Why? | `crashes`, `events --level error`, `watch` (exit codes), `crashdumps on` before reproducing |
| What does the app log? | `read <file> --new` / `--grep` / `--between 12:02 12:05` (the lines of a time span), `dbg` (OutputDebugString), `logs <job>` |
| Which DLL version did it load, from where? | `modules <pid> --grep <name>` |
| Who holds this file open? | `locks <file>` |
| Can it reach the server? Who listens on the port? | `net check host:port`, `net --listen`, `net --name <app>` |
| What did the installer / update change? | `envsnap --save before` ... `envsnap --diff before` (`--dir`, `--reg`) |
| What is in this window? Press a button, fill a field | `ui <window>` (controls as text, with their state: selected, checked, expanded), `ui click <name>`, `ui rclick <name>` (context menu), `ui dblclick <name>`, `ui set <name> <text>`, `ui get <name>` (one control's line: compare before and after) |
| Does it work by touch? | `touch tap <x> <y>`, `touch dtap` (double tap), `touch hold`, `touch drag`, `ui tap <name>`, `ui dtap <name>`: real touch input, also on a PC without a touch screen |
| What did that tap or click land on? Did the list scroll, the slider move? | `ui --at @x,y` (the control at that point, what it lies in and what is inside it, with class names), `ui <window> --grep <name>` before and after: a list says `(scroll v=52.8%)`, a slider `=0.739 (0..1)`, the window line has its position and size |
| What does the screen show? Is a menu open? | `windows` first (cheap; an open menu or drop-down is a `popup` line), `ui` for the controls; `screenshot --window <title> --scale 0.5` when you need pixels |
| Where is the time going (deep)? | `wpr start cpu file` ... `wpr stop` (needs an elevated target) |

## Running things

```
agentprobe exec 'Get-ChildItem C:\App | Select-Object -First 5'     # PowerShell; exit code is the remote exit code
agentprobe exec --shell cmd 'dir /b C:\App'
agentprobe exec --shell ps32 '...'                                   # 32-bit PowerShell (what a 32-bit app sees)
agentprobe exec --timeout 10m -f .\repro.ps1 -Port 8080 -Fast       # send a local script and run it; what follows
                                                                     # the file name goes to its param() block
agentprobe start --name build 'msbuild App.sln'                      # background job (a command); survives disconnects
agentprobe start --name app --exe 'C:\App\MyApp.exe'                 # an app with a window: the job IS the app
agentprobe start --name app --exe 'C:\App\MyApp.exe' --verbose -x 1  # what follows the path goes to the APP
agentprobe start --env COREHOST_TRACE=1 --cwd 'C:\Temp' --exe 'C:\App\MyApp.exe'   # options go BEFORE --exe
agentprobe start --lnk 'C:\Users\Public\Desktop\My App.lnk'         # as the person at the PC starts it
agentprobe logs build          # only output that is new since the last call
agentprobe wait --window "MyApp" --timeout 30s
agentprobe wait --idle MyApp --below 25                              # "finished starting" for an app that never idles
agentprobe kill app
```

- Quote the command with single quotes so your local shell leaves `$` and `|` alone. When the command itself
  needs both kinds of quotes, or holds C# for `Add-Type`, do not fight the quoting: save it as a file and send
  it with `exec -f` (nested quotes on one line arrive broken).
- Save a script you send with `-f` as UTF-8. On the target it is stored with a byte-order mark, so Windows PowerShell
  reads Korean or other non-ASCII text in it correctly (run directly from a BOM-less file it would not).
- Start a GUI app with `start --exe`, not as a command: a shell does not wait for a windowed program, so that job
  would end at once while the app runs on untracked. With `--exe` the job has the app's pid and its exit code
  (crash codes and .NET host errors are named), and `kill <job>` / `wait --job <job>` act on the app. The answer
  gives the folder it started in (`cwd=`, by default the exe's own folder, as a double-click does): an app can
  start from one folder and fail from another. An app that is gone within moments is reported with its exit code.
- A .NET app that shows no window, writes no log and leaves nothing in `crashes` or `events` failed in its host,
  before any of its own code ran: `deps <exe>` checks what the host needs (the app's DLL, `.runtimeconfig.json`,
  `.deps.json` and the files it lists, the runtime), and `deps <exe> --run` starts it once with the host's
  tracing on and says which mode the host took, what it wrote and the exit code. No administrator rights needed.
- A program that opens a network port from a new path (anything you `push` and run) makes Windows Firewall ask
  the person at the target to allow it. On an elevated target run `firewall allow <exe>` BEFORE starting it: that is
  the prompt's "Allow" as a rule for this session, and the prompt never appears. If one is already up,
  `firewall dismiss <pid>` closes it like "Cancel" (its buttons cannot be pressed remotely); the pid is the
  PROMPT's (the warning line gives the exact command), and `firewall dismiss` alone closes every such prompt.
  Where the target is not elevated, `start --dismiss-firewall --exe <app>` closes the prompt by itself when it appears. `windows`, `triage` and the end of `exec`/`start` output report such a
  SYSTEM PROMPT with its title (also UAC, credentials, SmartScreen, a packaged app's file picker); you cannot answer
  it remotely, so tell the user. This is found by the system process that shows the dialog, as Windows 11 does it:
  on a Windows that shows the firewall question another way it is not reported, so a silent stall after starting
  a server still deserves a `screenshot`. Connections to 127.0.0.1 work without an answer; once answered, the rule
  stays for that path. While such a prompt is up, the program it is about may not react to mouse input or to
  requests to close its windows (seen with the firewall prompt; `ui click` still worked), so answer or dismiss it
  before concluding that the app is broken.
- A window that `windows` marks `disabled` takes no mouse or keyboard input: a dialog that belongs to it is open
  (its own, or one of Windows). `click` on such a window says so instead of silently doing nothing. Close the
  dialog first; `ui click` goes through UI Automation instead of the mouse where the control allows it, and may
  still work.
- Swapping the installed app for a build under test: `start --replace MyApp.exe --exe <new build>` closes the
  running one by closing its windows, waits for it to end, then starts the other. Where Program Files cannot be
  written (a target without administrator rights), copy the installed folder into the workspace on the target and
  push only what changed over it: `exec 'Copy-Item -Recurse "C:\Program Files\MyApp" app'`, then
  `push .\fixed app` (a push into an existing folder merges), then `start --exe app\MyApp.exe`.
- An app that a script launches with `exec` keeps writing into that command's output for as long as it runs. `exec`
  stops listening when the script itself has ended and says so; start the app with `start --exe` to get its output
  apart (`logs <job>`).
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
agentprobe heap MyApp --windows                    # .NET: window objects per type: COUNT, SHOWN, CLOSED (closed but still referenced)
agentprobe heap MyApp --live --type EditorWindow   # .NET: how many are still referenced (closed 20, expect 0)
agentprobe heap MyApp --why EditorWindow           # .NET: what keeps one alive, root first
agentprobe dump MyApp --full --keep-remote # native heap, or a 32-bit .NET app: the dump stays on the target
                                           # (it is often gigabytes); without --keep-remote it comes to this PC
```

Crash on start:
```
agentprobe triage MyApp                   # not running: where it is installed, and the shortcut that starts it
agentprobe start --lnk 'C:\Users\Public\Desktop\My App.lnk'   # as the person starts it: exit code, named
agentprobe deps 'C:\App\MyApp.exe'        # missing DLL / x86-x64 mismatch / missing .NET runtime / what the .NET host needs
agentprobe deps 'C:\App\MyApp.exe' --run  # .NET app that just vanishes: what its host decided, and its exit code
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
`ui` prints each control's state as the control reports it: `(selected)` on the current tab or list item,
`(checked)`/`(unchecked)`, `(expanded)`/`(collapsed)`, and `(maximized)`/`(minimized)`/`(normal)` on the window's
line; `ui get <name>` before and after an action shows whether it took effect. `ui --grep` searches the whole
tree. When several controls have the same name the first is taken and the answer says so: `--nth 2` picks.
Fall back to `screenshot` → `click <x> <y>` (coordinates of that image; `--hover` only moves the pointer, for
tooltips and hover states). The `@x,y` that `ui` prints are SCREEN coordinates, not screenshot pixels: pass them as
`click @x,y`, never as two plain numbers after a scaled screenshot (a point outside the desktop is refused), `type <text>` (typed literally; use
`key enter` / `key tab` for those keys), `key ctrl+s`, `focus <title>` when a control has no name. Prefer `windows`
and `wait --window` over screenshots when you only need to know that something appeared. A screenshot is saved in
the developer PC's temp folder unless `-o` names a place; its path is printed.

A click goes to whatever window is in front at that point, which need not be the one you read the coordinates
from: agentprobe's own window on the target often lies over the app. `click` and `touch` say which window got the
input, and with `--window <title>` refuse when another window is in front (`--raise` brings the named one forward
first; `focus <title>` does the same on its own). `ui rclick`, `ui dblclick` and `ui tap` check this by themselves.
No input tool acts on agentprobe's own window: a click, touch, drag or scroll at a point on it is refused, and so are
`type` and `key` while it is the foreground window. The answer says so; `focus` the app and repeat. A bug that only shows with a finger needs `touch`, not `click`: the mouse does
not reproduce it. `windows --hwnd <title>` gives a window's handle for a script that needs one.

A screenshot is not always what the person at the PC sees. A window can keep itself out of screen captures
(players of protected video, password managers, meeting apps): `windows` marks it `no-capture`, and `screenshot`
names every such window in the area it captured and says whether it came out black or was left out. `ui` reads
those windows all the same. agentprobe's own "remote control" banner on the target is left out of screenshots too.

How far to trust an answer: counters (`ps`, `mem`, `handles`) are what Windows reports. `watch` calls a leak only
after two minutes of steady growth and says "too early" before that. With named processes its event list holds
those processes, what they started, any other process that ended with a crash code and any hang; starts and exits
of unrelated processes are counted, and `watch --all-events` lists them. A crash shows by the exit code, which can
only be read for processes the target may open: the report says how many exits it could not judge. `heap --type` looks at a type's own name (`--type Window` finds
MyApp.SettingsWindow, not all of System.Windows); `--ns` filters by namespace, and `--type "MyApp.*Window"` is a
pattern for the whole name. Every `heap` call stops the app for the moment its snapshot is taken (the "snapshot
taken in N ms" of the answer): ask for several types in one call (`--type "A,B"`) rather than one call each.
`heap --show` prints fields, not WPF dependency properties, and enums as numbers. `threads` shows a thread's name
only when the app set one that Windows knows (.NET Core and later, native code); .NET Framework names stay empty.
`read --between` goes by the time of day at the start of each line and knows nothing about dates. `stacks` names internal functions as
module+offset unless a PDB is present, and cannot walk managed/JIT frames. `trace` says so if events were dropped.
`kill --pid` ends that one process; add `--tree` to include what it started.

## Several targets

`-t <name>` picks one, `-t a,b` or `-t "LAB-*"` or `-t all` runs on many at once: identical results are printed once
with the list of targets that produced them. `agentprobe name -t LAB-PC07 gpu-rig` gives a target a short name.
`agentprobe list --all` also shows targets that went offline, with their last command. ADDRESS is the address
the PC connects from; a PC with several network adapters is listed with its other addresses below the table.
STATE is about commands (`busy(1) 42m` = one has been running that long, `idle 12s` = the last one ended then, or the PC connected then and has run none,
`paused` = the person there paused remote commands); CONNECTED is when the PC connected.
`-t local` runs a tool on the developer PC itself.

## Versions and updates

`agentprobe list` adds a line when a newer release exists (`agentprobe update`) or when a target runs another
version than this PC (`agentprobe -t <name> update`: sends this PC's exe; the target restarts agentprobe and its
session, approval and workspace carry over). Tell the user instead of updating on your own: an update restarts
agentprobe's background process, and a command you are waiting for ends with exit 75 (fetch it with `result <#>`).
Both commands hold back with exit 4 and an explanation while commands are running; `--force` is the user's call.
`update --check` (with or without `-t`) only reports. If your shell is a pseudo-terminal on a Korean Windows and
agentprobe's own messages come out in Korean, set `AGENTPROBE_LANG=en`.

## Exit codes

The remote command's own exit code for `exec`; `0` success; `2` usage; `3` no such target; `4` an update held back
because something is running (`--force` goes ahead); `5` paused by the person at the target (nothing was run: ask
the user); `75` connection lost
(the command may still be running: see `history`); `124` timeout; `130` cancelled. Crash codes are decoded, e.g.
`0xC0000005 access violation`, `0xC0000135 DLL not found`, `0xC000007B x86/x64 mismatch`, `0xE0434352 .NET exception`.
Codes from `0x80008081` to `0x800080A7` come from the .NET host, which gave up before the app's own code ran
(`0x80008096` the runtime it asks for is not installed, `0x8000809A` its DLL is missing, `0x80008081` the host did not
take the exe for an app): `deps <exe>` and `deps <exe> --run` say why.

Full option lists: `agentprobe help <command>`.
