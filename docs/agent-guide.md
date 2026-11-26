# agentprobe: guide for AI agents

You are on the developer PC. `agentprobe` lets you run commands and diagnostics on another Windows PC (the *target*)
that a person has shared with this PC. Everything you do is shown on the target's screen and written to a log there.

If `agentprobe` is not found, it is not on PATH yet: call the exe by its full path, or ask the user to run
`agentprobe.exe setup` once and open a new terminal.

This is the short guide. Topics to read when you need them are listed at the end (`agentprobe guide <topic>`).

## Rules of thumb

1. **Look before you run.** `agentprobe list` shows connected targets. For work of more than a few commands name
   the target from the start (`-t <name>`, or set `AGENTPROBE_TARGET=<name>`): another PC may connect while you work.
2. **Start with `agentprobe triage`** (add the app's name: `triage MyApp`). It tells you which tool to use next.
3. **Prefer a built-in tool over `exec`.** Built-ins answer in a few compact lines; a PowerShell pipeline that does the
   same costs several times more tokens and is easy to get subtly wrong.
4. **Every command is a turn, and every turn carries the whole conversation again.** So:
   - An action (`ui click`, `ui set`, `click`, `key`, `type`) already says what it changed: the window or menu that
     opened with its text and buttons, the control that reads differently, the program ending. Do not follow it
     with `windows`, `ui` or `screenshot` to see its effect.
   - Say what you expect instead of looking: `expect --ui "#status" --has Saved` is one line, pass or fail.
   - A sequence you will repeat (push the build, start it, drive it, check it) goes in a file and runs as ONE
     command: `agentprobe run steps.txt` (see below).
   - A crash or hang on the target is added to the next command's answer by itself: no need to ask `crashes`
     after every step.
5. **Filter on the target.** Use `--grep`, `--tail`, `--since`, `--top`, `--new` so only the relevant lines come back.
   Read a window again with `ui --diff` (only what changed), take a picture with `screenshot --diff` / `--control`.
6. **Do not poll.** `wait` blocks until something happens (also a control: `wait --ui "#status" --has Done`), and
   `watch` records trends while a test runs.
7. **Long output is cut for you** (head and tail, by lines and by characters) and saved to a local file whose path
   is printed. Search that file locally instead of re-running with `--full`. For a build or an installer use
   `exec --quiet-ok`: nothing but the last lines when it succeeds.
8. **After a dropped connection or a new session, run `agentprobe history`** to see how far the work got
   (`result <#>` returns an earlier output). Its OUT column is how much each command wrote: where your context went.
9. Leave the target as you found it. Things agentprobe itself changed are undone by `agentprobe end`; things you
   changed with `exec` are yours to undo.
10. **`paused by the person at the target` (exit 5) means stop.** Someone at that PC needs it. Nothing was run. Do not
    retry in a loop: tell the user, and go on when they say so.

## Which tool for which question

| Question | Command |
|---|---|
| What is wrong right now? | `triage [app]` |
| What machine is this (OS build, runtimes, locale, GPU driver)? | `sys` |
| Why does it work on my PC but not there? | `compare <target> local` (add `--dir <app folder>`) |
| Why does the app not start? | `triage <app>` (where it is installed), `start --exe <path>` or `start --lnk <shortcut>` (the exit code, named), `deps <exe>`, `deps <exe> --run` (.NET host), then `stat <exe>`, `crashes`, `events` |
| Is it running? How big? | `ps <name>`, `proc <name>` |
| What is using the CPU? Which thread? | `top`, `threads <pid>` (`--grep <name>`, `--by-name`) |
| Is it frozen? Where is it stuck? | `windows` (NOT RESPONDING), `threads <pid>`, `stacks <pid>`, then `dump <pid>` |
| Is it leaking memory / handles / GDI objects? | `watch start <name>` ... run scenario for 2+ minutes ... `watch`; `handles <pid> --interval 30s`; `vmmap <pid> --interval 30s` |
| .NET app: how many objects of a type are alive, and what holds them? | `heap <name> --windows`, `heap <name> --live --type "A,B"`, `heap <name> --why <Type>`, `heap <name> --show <Type> --fields a,b` |
| Which file or registry access fails? | `trace start <name>` ... reproduce ... `trace` (needs an elevated target) |
| Did it crash? Why? | `crashes`, `events --level error`, `watch` (exit codes), `crashdumps on` before reproducing |
| What does the app log? | `read <file> --new` / `--grep` / `--between 12:02 12:05`, `dbg` (OutputDebugString), `logs <job>` |
| Which DLL version did it load, from where? | `modules <pid> --grep <name>` |
| Who holds this file open? | `locks <file>` |
| Can it reach the server? Who listens on the port? | `net check host:port`, `net --listen`, `net --name <app>` |
| What did the installer / update change? | `envsnap --save before` ... `envsnap --diff before` (`--dir`, `--reg`) |
| What is in this window? Press a button, fill a field | `ui <window>` (controls as text with their state), `ui click <name>`, `ui set <name> <text>`, `ui get <name>`, `ui rclick` / `dblclick` |
| Did that have the effect I expect? | it is in the action's own answer; later: `expect --ui <name> --has <regex>`, `wait --ui ...`, `ui <window> --diff` |
| Does it work by touch? | `touch tap <x> <y>`, `touch dtap`, `touch hold`, `touch drag`, `ui tap <name>`, `ui dtap <name>` |
| What does the screen show? Is a menu open? | `windows` first (cheap; an open menu is a `popup` line), `ui` for the controls; pixels only when you must judge how something looks: `screenshot --control <name>`, `--window <title> --scale 0.5`, `--diff` |
| Is it black / is the video moving? | `screenshot --window <title> --stats` (numbers, no image), twice with `--diff --stats` |
| Where is the time going (deep)? | `wpr start cpu file` ... `wpr stop` (needs an elevated target) |

## Running things

```
agentprobe exec 'Get-ChildItem C:\App | Select-Object -First 5'     # PowerShell; exit code is the remote exit code
agentprobe exec --shell cmd 'dir /b C:\App'
agentprobe exec --timeout 10m -f .\repro.ps1 -Port 8080              # send a local script and run it
agentprobe exec --quiet-ok --timeout 20m 'msbuild App.sln'           # success: its last 5 lines; failure: the output
agentprobe start --name app --exe 'C:\App\MyApp.exe' --verbose      # an app with a window: the job IS the app
agentprobe start --replace MyApp.exe --exe app\MyApp.exe             # close the running one, start the build under test
agentprobe logs app            # only output that is new since the last call
agentprobe wait --window "MyApp" --timeout 30s
agentprobe kill app
```

- agentprobe's own options go BEFORE the command (`exec --timeout 5m '<command>'`). Quote the command with single
  quotes; when it needs both kinds of quotes, save it as a file and send it with `exec -f`.
- Start a GUI app with `start --exe`, not as a command. stdin is closed: pass `-Force`/`-Confirm:$false`/`/quiet`.
- The default folder is the session workspace: `agentprobe push .\bin\Release build` puts a folder there. It is
  deleted at `end`. Rights are those of whoever started agentprobe on the target; you cannot elevate remotely.

## A test as one command

```
# steps.txt: what you would type after `agentprobe`, one per line
push .\bin\Release app
start --replace MyApp.exe --exe app\MyApp.exe
wait --window "MyApp" --timeout 30s
ui click "Open" --window MyApp
ui set "#fileName" "report.txt" --window MyApp
key enter
expect --ui "#status" --has Loaded --in MyApp
show ui get "#result" --window MyApp
```

`agentprobe run steps.txt` answers `ok: 8 steps in 9.4s` after the output of the `show` step. When a step fails
it stops there and prints that step's output, the target's `windows` and the path of a screenshot. Fix, run it
again: one turn each time instead of eight. `try` in front of a step lets it fail; `sleep 500ms` pauses.
`agentprobe run --help` has the rest.

## Exit codes

The remote command's own exit code for `exec`; `0` success; `1` a tool's "no" (`expect` failed); `2` usage;
`3` no such target; `4` an update held back; `5` paused by the person at the target (ask the user); `75` connection
lost (the command may still be running: see `history`); `124` timeout; `130` cancelled. Crash codes are decoded,
e.g. `0xC0000005 access violation`, `0xC0000135 DLL not found`, `0xE0434352 .NET exception`; `0x80008081` to
`0x800080A7` come from the .NET host (`deps <exe>` says why).

Full option lists: `agentprobe help <command>`.

<!-- topic: ui | driving the UI by name, coordinates and touch; what a screenshot shows and what it does not -->
## Driving the UI

Prefer names over pixels. `ui "<window title>"` lists the controls; `ui click "Save"`,
`ui set "#fileName" "report.txt"` and `ui get "#status"` act on them, and keep working when the layout or DPI changes.
`ui` prints each control's state as the control reports it: `(selected)` on the current tab or list item,
`(checked)`/`(unchecked)`, `(expanded)`/`(collapsed)`, and `(maximized)`/`(minimized)`/`(normal)` on the window's
line, which also has the window's position and size; a list says `(scroll v=52.8%)`, a slider `=0.739 (0..1)`.
`ui --grep` searches the whole tree. When several controls have the same name the first is taken and the answer says
so: `--nth 2` picks.

What `ui` leaves out, to keep it short:
- positions. `ui --pos` adds each control's center as `@x,y` (screen coordinates); `ui get <name>` always has it,
  and so does a control that has neither name nor id. You need them only for `click`, `touch`, `drag`, `scroll`.
- a long run of list, tree or grid items: the first 8 and any selected one are listed, with a line that says how
  many are not. `--items` lists them all, `--grep` finds one.
- what did not change: `ui <window> --diff` prints only the controls that appeared, went or read differently since
  your last `ui` of that window.

Every action answers with what it did and what changed in the moment after (about a second):

```
invoked Button "Delete"
changed within 0.6s:
  + window "Confirm" (MyApp.exe pid 4120) opened, 420x180
      Text "Delete 3 files?"
      Button "Yes"
      Button "No"
  ~ window "MyApp" (MyApp.exe pid 4120) is now disabled: it takes no input, usually because a dialog that belongs to it is open
```

`~` reads differently (with what it read before), `+` appeared, `-` went. `no change seen within 0.5s` is an answer
too: the click did nothing visible in that time. What happens later is for `wait --ui` / `expect`; `--no-watch`
leaves the look out (a quick series of keys). Touch (`touch`, `ui tap`, `ui dtap`) does not look unless you add
`--watch`: check its effect with `expect`, `ui get` or `ui <window> --diff`.

`ui click` uses the control's own action where there is one and the mouse otherwise; a button that can only be
toggled (a WPF ToggleButton) is clicked with the mouse, because toggling it would flip its state without running the
app's click handler, and `--mouse` forces the mouse for any control. `ui set` reads the value back: it fails when
the control kept its old value, and for a combo box it writes into the edit field inside.

Fall back to `screenshot` then `click <x> <y>` (coordinates of that image; `--hover` only moves the pointer, for
tooltips and hover states). The `@x,y` that `ui --pos` prints are SCREEN coordinates, not screenshot pixels: pass
them as `click @x,y`, never as two plain numbers after a scaled screenshot (a point outside the desktop is refused).
`type <text>` types literally (use `key enter` / `key tab` for those keys), `key ctrl+s`, `focus <title>` when a
control has no name. Prefer `windows` and `wait --window` over screenshots when you only need to know that
something appeared.

A click goes to whatever window is in front at that point, which need not be the one you read the coordinates
from: agentprobe's own window on the target often lies over the app. `click` and `touch` say which window got the
input, and with `--window <title>` refuse when another window is in front (`--raise` brings the named one forward
first; `focus <title>` does the same on its own). `ui rclick`, `ui dblclick` and `ui tap` check this by themselves.
No input tool acts on agentprobe's own window: a click, touch, drag or scroll at a point on it is refused, and so are
`type` and `key` while it is the foreground window. The answer says so; `focus` the app and repeat. A bug that only
shows with a finger needs `touch`, not `click`: the mouse does not reproduce it. `windows --hwnd <title>` gives a
window's handle for a script that needs one.

A window that `windows` marks `disabled` takes no mouse or keyboard input: a dialog that belongs to it is open
(its own, or one of Windows). `click` on such a window says so instead of silently doing nothing. Close the
dialog first; `ui click` goes through UI Automation instead of the mouse where the control allows it, and may
still work.

### Screenshots

An image costs by its pixels, not by its file size: ask for the part you need.
`screenshot --control <name>` is one control, `--region X,Y,W,H` a rectangle of the screen, `--window <title>` one
window, `--scale 0.5` half the size. `screenshot --diff` gives only the part that changed since your previous
screenshot of the same area, and one line of text with no image when nothing changed. `screenshot --stats` gives
numbers instead of a picture (average color, share near black and near white), which is enough for "is the window
black?"; with `--diff` also how many pixels changed. A screenshot is saved in the developer PC's temp folder unless
`-o` names a place; its path is printed.

`screenshot` without `--window` is the screen as it is: whatever lies in front is in the picture, agentprobe's own
window included (`share --minimized` on the target keeps that one out of the way). `screenshot --window <title>`
asks the window to draw itself, with the dialogs and message boxes it owns, so it comes out whole even when it is
covered; the answer says when it is covered on the screen, because a click there would go to what is in front.
`windows` marks a window that is mostly behind others as `covered`.

A screenshot is not always what the person at the PC sees. A window can keep itself out of screen captures
(players of protected video, password managers, meeting apps): `windows` marks it `no-capture`, and `screenshot`
names every such window in the area it captured and says whether it came out black or was left out. `ui` reads
those windows all the same. agentprobe's own "remote control" banner on the target is left out of screenshots too.

<!-- topic: exec | running commands, scripts and apps on the target: quoting, jobs, swapping a build, what goes wrong -->
## Running things, in detail

```
agentprobe exec --shell ps32 '...'                                   # 32-bit PowerShell (what a 32-bit app sees)
agentprobe exec --timeout 10m -f .\repro.ps1 -Port 8080 -Fast       # what follows the file name goes to its param() block
agentprobe start --name build 'msbuild App.sln'                      # background job (a command); survives disconnects
agentprobe start --name app --exe 'C:\App\MyApp.exe' --verbose -x 1  # what follows the path goes to the APP
agentprobe start --env COREHOST_TRACE=1 --cwd 'C:\Temp' --exe 'C:\App\MyApp.exe'   # options go BEFORE --exe
agentprobe start --lnk 'C:\Users\Public\Desktop\My App.lnk'         # as the person at the PC starts it
agentprobe wait --idle MyApp --below 25                              # "finished starting" for an app that never idles
```

- agentprobe's own options go BEFORE the command (`exec --full '<command>'`, `exec --timeout 5m '<command>'`):
  after it they would be sent to the target as part of the command, so that form is refused.
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
- Swapping the installed app for a build under test: `start --replace MyApp.exe --exe <new build>` closes the
  running one by closing its windows, waits for it to end, then starts the other. Where Program Files cannot be
  written (a target without administrator rights), copy the installed folder into the workspace on the target and
  push only what changed over it: `exec 'Copy-Item -Recurse "C:\Program Files\MyApp" app'`, then
  `push .\fixed app` (a push into an existing folder merges), then `start --exe app\MyApp.exe`.
- The script files `exec -f` runs from are removed from the workspace when the command has ended.
- A test window that a script opens from a process started hidden (`Start-Process -WindowStyle Hidden`) stays
  hidden: Windows applies "hidden" to the first window that process shows. Start the helper with `start --exe`, or
  call `Show()` on the window a second time.
- An app that a script launches with `exec` keeps writing into that command's output for as long as it runs. `exec`
  stops listening when the script itself has ended and says so; start the app with `start --exe` to get its output
  apart (`logs <job>`).
- stdin is closed: anything that prompts fails immediately instead of hanging. Pass `-Force`/`-Confirm:$false`/`/quiet`.
- Commands run as the logged-on user **in their desktop session**: a GUI app you start is visible there.
- Rights are those of whoever started agentprobe on the target (`list` shows `user` or `admin`). You cannot elevate
  remotely. If a tool says it needs an elevated target, tell the user to restart agentprobe there with
  *Run as administrator*.
- Output: a line that comes several times in a row is printed once with its count; `read` (log lines), `logs` and
  `dbg` print lines that differ only in their numbers as the first one plus how many followed and the last one
  (`--no-fold` prints them all).

<!-- topic: firewall | Windows Firewall and other system prompts (UAC, credentials, SmartScreen) that block a test -->
## System prompts and the firewall

A program that opens a network port from a new path (anything you `push` and run) makes Windows Firewall ask
the person at the target to allow it. On an elevated target run `firewall allow <exe>` BEFORE starting it: that is
the prompt's "Allow" as a rule for this session, and the prompt never appears. If one is already up,
`firewall dismiss <pid>` closes it like "Cancel" (its buttons cannot be pressed remotely); the pid is the
PROMPT's (the warning line gives the exact command), and `firewall dismiss` alone closes every such prompt.
Where the target is not elevated, `firewall allow` is not possible and the prompt comes back at every start:
`firewall dismiss --auto` closes every such prompt for the rest of the session (also the ones a script causes),
`start --dismiss-firewall --exe <app>` does it for one start. A closed prompt allows nothing, and Windows may keep
block rules for that program's path: `firewall leftovers` lists them, `end` says how many stay, and removing them
takes an elevated target (`firewall cleanup`).

`windows`, `triage` and the end of `exec`/`start` output report such a SYSTEM PROMPT with its title (also UAC,
credentials, SmartScreen, a packaged app's file picker); you cannot answer it remotely, so tell the user. This is
found by the system process that shows the dialog, as Windows 11 does it: on a Windows that shows the firewall
question another way it is not reported, so a silent stall after starting a server still deserves a `screenshot`.
Connections to 127.0.0.1 work without an answer; once answered, the rule stays for that path. While such a prompt
is up, the program it is about may not react to mouse input or to requests to close its windows (seen with the
firewall prompt; `ui click` still worked), so answer or dismiss it before concluding that the app is broken.

<!-- topic: sessions | typical sessions step by step: a leak hunt, a crash on start, a reboot in the middle -->
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
History also survives a reboot of the target and an update of agentprobe there (those entries have no output any
more). `orphaned` runs finished after the connection dropped; `agentprobe result <#>` returns their output.

<!-- topic: trust | how far to trust an answer: what each tool measures, estimates or cannot see -->
## How far to trust an answer

Counters (`ps`, `mem`, `handles`) are what Windows reports. `watch` calls a leak only after two minutes of steady
growth and says "too early" before that. With named processes its event list holds those processes, what they
started, any other process that ended with a crash code and any hang; starts and exits of unrelated processes are
counted, and `watch --all-events` lists them. A crash shows by the exit code, which can only be read for processes
the target may open: the report says how many exits it could not judge.

What an action says changed is what was different about a second after it: the top-level windows, and the controls
of the window acted on (the first 400; with more, only controls that read differently are reported, and the answer
says so). "no change seen" means none in that time and in those places, not that nothing will happen. Touch
does not look by default: on the machine that builds agentprobe, the first touch of a logon session sent with the
look around it reached a button without pressing it, for a reason that is not known. The crash
line added to a command's answer comes from the Application event log, which Windows writes a moment after the
crash: a crash at the very end of a command is reported with the next one.

`history`'s OUT is characters of text produced on the target; tokens are roughly a quarter of that for English and
it is an estimate. `screenshot --stats` counts pixels of the captured image, at the scale asked for; "the most common
color" is rounded to 16 steps per channel.

`heap --type` looks at a type's own name (`--type Window` finds MyApp.SettingsWindow, not all of System.Windows);
`--ns` filters by namespace, and `--type "MyApp.*Window"` is a pattern for the whole name. Every `heap` call stops
the app for the moment its snapshot is taken (the "snapshot taken in N ms" of the answer): ask for several types in
one call (`--type "A,B"`) rather than one call each. `heap --show` prints fields, not WPF dependency properties, and
enums as numbers. `threads` shows a thread's name only when the app set one that Windows knows (.NET Core and later,
native code); .NET Framework names stay empty. `read --between` goes by the time of day at the start of each line
and knows nothing about dates. `stacks` names internal functions as module+offset unless a PDB is present, and
cannot walk managed/JIT frames. `trace` says so if events were dropped. `kill --pid` ends that one process; add
`--tree` to include what it started.

<!-- topic: targets | several targets at once, which PC a command goes to, versions and updates -->
## Several targets, versions and updates

With one target you can omit `-t`. When another PC connects while you work, commands without `-t` keep going to the
PC you were using and say so in a `[...]` line (for half an hour after the last one; `end` and `reboot` always need
the name then). When the PC you were using drops and another is connected, an unnamed command is refused rather
than run there. `list` shows when each PC connected.

`-t <name>` picks one, `-t a,b` or `-t "LAB-*"` or `-t all` runs on many at once: identical results are printed once
with the list of targets that produced them. `agentprobe name -t LAB-PC07 gpu-rig` gives a target a short name.
`agentprobe list --all` also shows targets that went offline, with their last command. ADDRESS is the address
the PC connects from; a PC with several network adapters is listed with its other addresses below the table.
STATE is about commands (`busy(1) 42m` = one has been running that long, `idle 12s` = the last one ended then, or the PC connected then and has run none,
`paused` = the person there paused remote commands); CONNECTED is when the PC connected.
`-t local` runs a tool on the developer PC itself (not the ones that keep state between calls: `watch`, `start`,
`ui --diff`, `screenshot --diff` and the like need a real session).

`agentprobe list` adds a line when a newer release exists (`agentprobe update`) or when a target runs another
version than this PC (`agentprobe -t <name> update`: sends this PC's exe; the target restarts agentprobe and its
session, approval and workspace carry over). Tell the user instead of updating on your own: an update restarts
agentprobe's background process, and a command you are waiting for ends with exit 75 (fetch it with `result <#>`).
Both commands hold back with exit 4 and an explanation while commands are running; `--force` is the user's call.
`update --check` (with or without `-t`) only reports. If your shell is a pseudo-terminal on a Korean Windows and
agentprobe's own messages come out in Korean, set `AGENTPROBE_LANG=en`.

`paused by the person at the target` (exit 5): someone at that PC pressed the space bar in agentprobe's window
(Enter there resumes) because they need the PC. Commands and jobs that were already running go on.
