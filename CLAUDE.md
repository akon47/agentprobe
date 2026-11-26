# agentprobe

One Windows exe that lets an AI agent (or a person) on a developer PC debug and test another Windows PC on the same
network. `README.md` describes the product; `docs/agent-guide.md` (embedded in the exe) is how agents use it.

**Public repository.** No personal data, machine names, internal addresses, credentials or references to private
projects in code, comments, docs, tests, examples or commit messages. Use neutral examples (`DEV-PC`, `LAB-PC01`,
`MyApp.exe`, `192.168.0.20`).

## Where things are

`src/AgentProbe/`: `Program.cs` picks the role (share = target, daemon, CLI). `Transport/` is the handshake and
encrypted frames, `Target/` the shared PC's side, `Controller/` the developer PC's side (daemon, CLI, MCP, dashboard,
`Home.cs` start screen), `Tools/` one file per tool group, `Native/` P/Invoke and snapshots.

## Commands

```powershell
dotnet build src/AgentProbe                              # debug build
dotnet publish src/AgentProbe -c Release -o publish      # the native exe
tests\verify.ps1 -Session                                # the test suite, about 4 minutes
```

- Publishing needs the Visual Studio "Desktop development with C++" workload and `vswhere.exe` on `PATH`
  (`%ProgramFiles(x86)%\Microsoft Visual Studio\Installer`).
- One PC can be developer PC and target at once: `agentprobe list`, then `agentprobe share --yes --name selftest`
  in another window, then `agentprobe -t selftest triage`. The exe is locked while either runs (`agentprobe stop`).

## Done means verified

- Run `tests\verify.ps1 -Session` before calling a change done (`-SkipInput` on a PC someone is using), and add a
  check to it for every bug fixed. It compares the tools' output with what Windows reports.
- Discovery, the handshake, reboot and admin-only tools (`trace`, `wpr`, `crashdumps`) also need a second PC.
- After changing the dashboard or the start screen, look at screenshots: the dashboard in both themes and languages,
  the start screen in Windows Terminal and the classic console at a small and a large size.

## Rules

- **It must not lie.** A wrong number, a guess shown as a fact, a truncated search reported as "no matches" or an
  error reported as "nothing found" is a serious bug. Approximate or incomplete output says so in the same line.
- **One exe, no install.** No services, drivers or extra files. The target never listens on a port.
- **Output is for an agent's context window.** Few lines, no decoration; filter and summarize on the target.
- **A command an agent does not have to run is the cheapest one.** Every turn carries the whole conversation again:
  an action answers with what it changed (`UiaChanges.cs`), a crash is added to the next answer (`CrashNotes.cs`),
  and what is left out or folded is said in the same line.
- **Undo at session end** everything a tool changed on the target (`TargetRuntime.AddUndo`), and only what this
  session changed.
- **The person at the target sees everything.** State-changing tools write an audit line
  (`ctx.Runtime.Audit.Write`). Never add a way to hide the banner or to edit the activity log remotely.
- **Trust is per key.** Never accept a developer PC key that nobody approved.
- **The CLI is the product; MCP is a thin door to it.** New features are CLI commands, not new MCP tools.
- **The dashboard is the only GUI**: one control height, radius and typeface, no native-default widgets.
- **The start screen is redrawn from a list of lines** on every change. Only ASCII and Korean are assumed to have a
  known width; anything else is measured or clipped.
- English for code, comments, agent-facing output and commits. Text a person reads (target PC, dashboard, start
  screen) is English and Korean, chosen from the OS language (`Lang.T`).

## Adding a tool

Add a `new Tool("name", "Group", "summary", """usage""", Handler)` to its group's `Tools` list (a new file is listed
in `ToolRegistry`). The CLI forwards any registered name, so nothing else changes. Use `ArgParser`, `Table` and
`Format`; mark read-only chatty tools `{ Quiet = true }`; document it in `docs/agent-guide.md` and both READMEs.

The guide is one file in two parts. What comes before the first `<!-- topic: name | summary -->` line is the core
that `agentprobe guide` prints and every agent keeps in its context: keep it short. Detail goes under a topic,
which is read with `agentprobe guide <name>` when it is needed.

## NativeAOT

- JSON only through the source-generated `JsonCtx`; add new serialized types there.
- P/Invoke with `[LibraryImport]`; callbacks are `[UnmanagedCallersOnly]`, passed as `delegate* unmanaged[Stdcall]`.
- No `await` in an `unsafe` context: keep pointer code in small synchronous helpers.
- Native structures are read by x64 offsets.

## Releasing

The version comes from the tag, not from the csproj. Either push a tag (`git tag v0.2.0; git push origin v0.2.0`)
or run the `build` workflow by hand with a version: it builds the exe with that version, runs the test suite, and
creates the tag and the release with `agentprobe.exe` and its SHA-256.

Released exes update themselves from that release (`Controller/Updater.cs`): they read `releases/latest` and expect
the assets `agentprobe.exe` and `agentprobe.exe.sha256` under exactly those names. Keep the csproj version equal to
the last release, or a local build offers to "update" itself to it. Anything that touches a running agentprobe is
tried in a separate instance (`AGENTPROBE_INSTANCE`, `AGENTPROBE_PORT`, and `AGENTPROBE_UPDATE_URL` for a local feed),
as `tests\verify.ps1` does.
