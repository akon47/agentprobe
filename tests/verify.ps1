<#
.SYNOPSIS
  Checks that agentprobe tells the truth: runs its tools on this PC and compares what they report with what
  Windows itself reports, and exercises behaviour that has a right answer (exit codes, file slicing, leak verdicts,
  UI automation, transfers).

.DESCRIPTION
  Uses `-t local` for most checks, so no second PC is needed. With -Session it also starts a daemon and a local
  target and tests the real remote path (exec, push/pull, history).
  A small test window and a short-lived helper process appear while it runs.

  Exit code = number of failed checks.

.EXAMPLE
  .\tests\verify.ps1 -Exe .\publish\agentprobe.exe -Session
#>
param(
    [string]$Exe = (Join-Path $PSScriptRoot '..\publish\agentprobe.exe'),
    [switch]$Session,
    # Skip the checks that send real mouse and keyboard input (use this on a PC someone is working on).
    [switch]$SkipInput
)

$ErrorActionPreference = 'Stop'
# A separate agentprobe instance (own background process, state folder and port) for everything below, so the suite
# never stops or talks to an agentprobe someone is working with on this PC.
$env:AGENTPROBE_INSTANCE = 'verify'
$env:AGENTPROBE_PORT = '48652'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false   # agentprobe writes UTF-8; decode it as such on any code page
$Exe = (Resolve-Path $Exe).Path
$work = Join-Path $env:TEMP ("agentprobe-verify-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $work | Out-Null
Push-Location $work   # agentprobe -t local writes into the current folder (dump --keep-remote): never the caller's
# The long form of the folder's path: %TEMP% can be a short (8.3) name, which Windows Firewall does not accept for a
# program and which would not match the long path agentprobe reports.
Add-Type -Namespace ApVerify -Name Paths -MemberDefinition '[DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern uint GetLongPathName(string path, System.Text.StringBuilder longPath, uint size);'
$longWork = New-Object System.Text.StringBuilder 1024
if ([ApVerify.Paths]::GetLongPathName($work, $longWork, 1024) -gt 0) { $work = $longWork.ToString() }
$script:pass = 0; $script:fail = 0; $script:failed = @()

function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:pass++; Write-Host "  ok    $name" -ForegroundColor DarkGreen }
    else { $script:fail++; $script:failed += $name; Write-Host "  FAIL  $name  $detail" -ForegroundColor Red }
}

# Quotes one argument the way Windows programs parse their command line (PowerShell 5.1 does not do this reliably).
function Quote([string]$a) {
    if ($a.Length -gt 0 -and $a -notmatch '[\s"]') { return $a }
    $s = $a -replace '(\\*)"', '$1$1\"' -replace '(\\+)$', '$1$1'
    return '"' + $s + '"'
}

# Runs agentprobe and returns @{ Out = text; Code = exit code }. Output is read as UTF-8; stderr follows stdout.
function AP {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Exe
    $psi.Arguments = ($args | ForEach-Object { Quote "$_" }) -join ' '
    $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
    $psi.WorkingDirectory = (Get-Location).Path   # .NET starts processes in the process's folder, which Push-Location does not change
    $psi.StandardOutputEncoding = [Text.Encoding]::UTF8; $psi.StandardErrorEncoding = [Text.Encoding]::UTF8
    $p = [System.Diagnostics.Process]::Start($psi)
    $err = $p.StandardError.ReadToEndAsync()
    $out = $p.StandardOutput.ReadToEnd()
    $p.WaitForExit()
    return @{ Out = ($out + $err.Result).Replace("`r`n", "`n").TrimEnd("`n"); Code = $p.ExitCode }
}

function Near([double]$a, [double]$b, [double]$relative, [double]$absolute) { [Math]::Abs($a - $b) -le [Math]::Max($absolute, [Math]::Abs($b) * $relative) }

Write-Host "agentprobe $((AP version).Out) - $Exe"
Write-Host "work folder: $work`n"

try {
    # ------------------------------------------------------------------ numbers vs Windows
    Write-Host "process numbers (compared with Get-Process / CIM)"
    $helper = Start-Process powershell -ArgumentList '-NoProfile', '-Command', 'Start-Sleep 600' -WindowStyle Hidden -PassThru
    Start-Sleep -Milliseconds 1500
    $gp = Get-Process -Id $helper.Id
    $r = AP -t local ps $helper.Id
    $line = ($r.Out -split "`n" | Where-Object { $_ -match "^\s*$($helper.Id)\s" } | Select-Object -First 1)
    Check 'ps finds the process by pid' ($null -ne $line) $r.Out
    if ($line) {
        $c = $line.Trim() -split '\s+'
        $gp.Refresh()
        Check 'ps parent pid' ([int]$c[1] -eq $PID) "got $($c[1]), expected $PID"
        Check 'ps image name' ($c[2] -eq 'powershell.exe') $c[2]
        Check 'ps private bytes (MB)' (Near ([double]$c[3]) ($gp.PrivateMemorySize64 / 1MB) 0.15 8) "got $($c[3]), Windows $([int]($gp.PrivateMemorySize64 / 1MB))"
        Check 'ps working set (MB)' (Near ([double]$c[4]) ($gp.WorkingSet64 / 1MB) 0.25 12) "got $($c[4]), Windows $([int]($gp.WorkingSet64 / 1MB))"
        Check 'ps handle count' (Near ([double]$c[5]) $gp.HandleCount 0.15 30) "got $($c[5]), Windows $($gp.HandleCount)"
        Check 'ps thread count' (Near ([double]$c[6]) $gp.Threads.Count 0.3 4) "got $($c[6]), Windows $($gp.Threads.Count)"
    }

    $r = AP -t local proc $helper.Id
    Check 'proc shows the command line' ($r.Out -match 'Start-Sleep 600') $r.Out
    Check 'proc shows the image path' ($r.Out -match [regex]::Escape($gp.Path)) $r.Out
    Check 'proc reports x64' ($r.Out -match "\(pid $($helper.Id)\)\s+x64") $r.Out

    $r = AP -t local handles $helper.Id
    if ($r.Out -match 'pid \d+: (\d+) handles') { $gp.Refresh(); Check 'handles total' (Near ([double]$Matches[1]) $gp.HandleCount 0.15 30) "got $($Matches[1]), Windows $($gp.HandleCount)" } else { Check 'handles total' $false $r.Out }
    Check 'handles lists object types' ($r.Out -match '(?m)^(Event|File|Key|Thread)\s+\d+') $r.Out

    $r = AP -t local modules $helper.Id --all
    if ($r.Out -match '(\d+) modules loaded') { Check 'modules count' (Near ([double]$Matches[1]) $gp.Modules.Count 0.1 3) "got $($Matches[1]), Windows $($gp.Modules.Count)" } else { Check 'modules count' $false $r.Out }
    Check 'modules lists ntdll with its path' ($r.Out -match 'ntdll\.dll\s+\S+\s+\S+\s+C:\\Windows\\System32\\ntdll\.dll') ''

    $r = AP -t local threads $helper.Id --interval 500ms
    Check 'threads runs and names a start module' ($r.Code -eq 0 -and $r.Out -match '\.dll\+0x|\.exe\+0x') $r.Out

    $r = AP -t local stacks $helper.Id --top 3
    Check 'stacks walks the threads' ($r.Code -eq 0 -and $r.Out -match 'ntdll') $r.Out

    Write-Host "`nsystem numbers"
    $os = Get-CimInstance Win32_OperatingSystem
    $cs = Get-CimInstance Win32_ComputerSystem
    $r = AP -t local sys
    $ubr = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').UBR
    Check 'sys OS build' ($r.Out -match "build $($os.BuildNumber)\.$ubr") ($r.Out -split "`n" | Select-Object -Index 1)
    Check 'sys logical processors' ($r.Out -match "\s$([Environment]::ProcessorCount) logical") ''
    Check 'sys machine name' ($r.Out -match [regex]::Escape($env:COMPUTERNAME)) ''

    $r = AP -t local mem
    $toBytes = { param($t) $n = [double]($t -replace '[^\d.]'); switch -Regex ($t) { 'G$' { $n * 1GB } 'M$' { $n * 1MB } 'K$' { $n * 1KB } default { $n } } }
    if ($r.Out -match 'physical\s+(\S+) total\s+(\S+) available') {
        Check 'mem physical total' (Near (& $toBytes $Matches[1]) $cs.TotalPhysicalMemory 0.03 0) "got $($Matches[1]), Windows $([Math]::Round($cs.TotalPhysicalMemory / 1GB, 2))G"
        $os = Get-CimInstance Win32_OperatingSystem
        Check 'mem physical available' (Near (& $toBytes $Matches[2]) ($os.FreePhysicalMemory * 1KB) 0.15 (512MB)) "got $($Matches[2]), Windows $([Math]::Round($os.FreePhysicalMemory / 1MB, 2))G"
    } else { Check 'mem physical line' $false $r.Out }
    if ($r.Out -match 'commit\s+(\S+) of (\S+) limit') {
        Check 'mem commit limit' (Near (& $toBytes $Matches[2]) ($os.TotalVirtualMemorySize * 1KB) 0.05 0) "got $($Matches[2]), Windows $([Math]::Round($os.TotalVirtualMemorySize / 1MB, 2))G"
    } else { Check 'mem commit line' $false $r.Out }

    Write-Host "`nnetwork"
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0); $listener.Start(); $port = $listener.LocalEndpoint.Port
    $r = AP -t local net --port $port
    Check 'net shows our listening port with our pid' ($r.Out -match "(?m)^\s*$port\s+127\.0\.0\.1\s+$([regex]::Escape((Get-Process -Id $PID).ProcessName))\.exe\($PID\)") $r.Out
    $r = AP -t local net check "127.0.0.1:$port" 'name.invalid:80'
    Check 'net check: open port is OK' ($r.Out -match "127\.0\.0\.1:$port`: OK") $r.Out
    Check 'net check: bad name is a DNS failure, exit 1' ($r.Out -match 'name\.invalid:80: DNS FAILED' -and $r.Code -eq 1) $r.Out
    $r = AP -t local wait --port $port --timeout 5s
    Check 'wait --port succeeds' ($r.Code -eq 0) $r.Out
    $listener.Stop()

    # ------------------------------------------------------------------ files
    Write-Host "`nfiles"
    $file = Join-Path $work 'lines.txt'
    1..500 | ForEach-Object { "line $_ value=$($_ * 7)" } | Set-Content $file -Encoding UTF8
    Check 'read --tail 3' ((AP -t local read $file --tail 3).Out.Trim() -eq "line 498 value=3486`nline 499 value=3493`nline 500 value=3500") ''
    Check 'read --head 2' ((AP -t local read $file --head 2).Out.Trim() -eq "line 1 value=7`nline 2 value=14") ''
    Check 'read --lines 100-101 numbers the lines' ((AP -t local read $file --lines 100-101).Out.Trim() -eq "100: line 100 value=700`n101: line 101 value=707") ''
    $g = (AP -t local read $file --grep 'value=35$').Out.Trim()
    Check 'read --grep returns exactly the match' ($g -eq '5: line 5 value=35') $g
    Check '-t local refuses commands that need a session' ((AP -t local read $file --new).Code -eq 2 -and (AP -t local watch start explorer).Code -eq 2) ''

    # A file in this PC's own ANSI code page: 949 on Korean Windows, 1252 on Western European ones, and so on.
    $acp = [int](Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Nls\CodePage').ACP
    if ($acp -eq 65001) {
        Write-Host '  skipped: the ANSI code page of this PC is UTF-8, so there is no separate ANSI decoding to check.' -ForegroundColor Yellow
    } else {
        $ansi = [Text.Encoding]::GetEncoding($acp)
        $sample = switch ($acp) { 949 { '한글 줄' } 932 { '日本語の行' } 936 { '中文行' } 950 { '中文行' } default { 'line ' + $ansi.GetString([byte[]](0xE0..0xE5)) } }
        $ansiFile = Join-Path $work 'ansi.txt'
        [IO.File]::WriteAllBytes($ansiFile, $ansi.GetBytes("$sample`r`nsecond"))
        $shown = (AP -t local read $ansiFile).Out
        Check "read decodes a file in the system ANSI code page (cp$acp)" ($shown.Contains($sample)) "expected '$sample', got: $shown"
    }
    $utf16 = Join-Path $work 'utf16.txt'; [IO.File]::WriteAllText($utf16, "유니코드 줄`r`nx", [Text.Encoding]::Unicode)
    Check 'read decodes UTF-16' ((AP -t local read $utf16).Out -match '유니코드 줄') ''

    $r = AP -t local stat $Exe
    $hash = (Get-FileHash $Exe -Algorithm SHA256).Hash.ToLower()
    Check 'stat sha256' ($r.Out -match $hash) ''
    Check 'stat image kind' ($r.Out -match 'x64 native console EXE') $r.Out
    Check 'stat size' ($r.Out -match "size\s+$((Get-Item $Exe).Length) ") ''

    $locked = Join-Path $work 'locked.bin'; $fs = [IO.File]::Open($locked, 'Create', 'ReadWrite', 'None')
    $r = AP -t local locks $locked
    Check 'locks names the process holding the file' ($r.Out -match "pid $PID\)") $r.Out
    $fs.Close()
    Check 'locks reports a free file as free' ((AP -t local locks $locked).Out -match 'not held open') ''

    $r = AP -t local ls $work --sort size
    Check 'ls lists the folder' ($r.Out -match 'lines\.txt' -and $r.Out -match 'entries') $r.Out

    # A 70 MB log with the only interesting line at the very end, and Korean text throughout:
    # searching must cover all of it, and reading its tail must not garble multi-byte characters.
    $big = Join-Path $work 'big.log'
    $sw = New-Object IO.StreamWriter($big, $false, (New-Object Text.UTF8Encoding $false))
    for ($i = 1; $i -le 1100000; $i++) { $sw.Write('2026-01-01 12:00:00 정보 요청 처리 완료 request-id=' + $i + " ok`n") }
    $sw.Write("NEEDLE-AT-THE-END 오류 발생`n"); $sw.Close()
    $r = AP -t local read $big --grep 'NEEDLE-AT-THE-END'
    Check 'read --grep searches the whole of a 70 MB file' ($r.Out.Trim() -eq '1100001: NEEDLE-AT-THE-END 오류 발생') $r.Out.Substring(0, [Math]::Min(120, $r.Out.Length))
    $r = AP -t local read $big --grep 'request-id=1000000 '
    Check 'read --grep reports the right line number deep in the file' ($r.Out.Trim() -match '^1000000: ') $r.Out.Substring(0, [Math]::Min(80, $r.Out.Length))
    $r = AP -t local read $big --tail 2
    Check 'read --tail on a big file returns exactly the last lines' ($r.Out.Trim() -eq "2026-01-01 12:00:00 정보 요청 처리 완료 request-id=1100000 ok`nNEEDLE-AT-THE-END 오류 발생") $r.Out
    $r = AP -t local read $big
    Check 'read (no filter) of a big UTF-8 file: tail shown, no garbled text' ($r.Out -match 'NEEDLE-AT-THE-END 오류 발생' -and $r.Out -notmatch [char]0xFFFD -and $r.Out -match 'showing its last 200 lines') ''
    $r = AP -t local read $big --lines 500000-500001
    Check 'read --lines deep in a big file' ($r.Out.Trim() -eq "500000: 2026-01-01 12:00:00 정보 요청 처리 완료 request-id=500000 ok`n500001: 2026-01-01 12:00:00 정보 요청 처리 완료 request-id=500001 ok") $r.Out

    # kill must take down what was named, and nothing else unless --tree is given.
    $parent = Start-Process powershell -ArgumentList '-NoProfile', '-Command', 'Start-Process powershell -ArgumentList ''-NoProfile'',''-Command'',''Start-Sleep 300'' -WindowStyle Hidden; Start-Sleep 300' -WindowStyle Hidden -PassThru
    Start-Sleep 3
    $child = Get-CimInstance Win32_Process -Filter "ParentProcessId=$($parent.Id) AND Name='powershell.exe'" | Select-Object -First 1
    if ($child) {
        $r = AP -t local kill --pid $parent.Id
        Start-Sleep 1
        Check 'kill --pid ends that process' ($r.Code -eq 0 -and -not (Get-Process -Id $parent.Id -ErrorAction SilentlyContinue)) $r.Out
        Check 'kill --pid leaves its children alone' ([bool](Get-Process -Id $child.ProcessId -ErrorAction SilentlyContinue)) ''
        Stop-Process -Id $child.ProcessId -Force -ErrorAction SilentlyContinue
    } else { Check 'kill test: child process started' $false ''; Stop-Process -Id $parent.Id -Force -ErrorAction SilentlyContinue }

    $r = AP -t local wait --job 99 --timeout 2s
    Check 'wait --job with an unknown job is an error, not success' ($r.Code -ne 0 -and $r.Code -ne 124) "$($r.Code) $($r.Out)"
    $l6 = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::IPv6Loopback, 0); $l6.Start(); $p6 = $l6.LocalEndpoint.Port
    Check 'wait --port sees an IPv6-only listener' ((AP -t local wait --port $p6 --timeout 3s).Code -eq 0) ''
    $l6.Stop()
    $r = AP share --trust 3F9A
    Check 'share --trust refuses a shortened key' ($r.Code -eq 2) "$($r.Code) $($r.Out)"

    # An app that never goes fully idle: the limit can be raised, and a timeout says what was measured.
    $r = AP -t local wait --idle $helper.Id --below 50 --timeout 15s
    Check 'wait --idle --below: an idle process is below the given limit' ($r.Code -eq 0 -and $r.Out -match 'below 50% of a core') "$($r.Code) $($r.Out)"
    $spin = Start-Process powershell -ArgumentList '-NoProfile', '-Command', 'while ($true) { }' -WindowStyle Hidden -PassThru
    $r = AP -t local wait --idle $spin.Id --below 20 --timeout 5s
    Stop-Process -Id $spin.Id -Force
    Check 'wait --idle --below: a busy process times out, and the message says what it used' ($r.Code -eq 124 -and $r.Out -match 'last used (\d+)(\.\d+)?% of a core' -and [double]$Matches[1] -gt 50) "$($r.Code) $($r.Out)"
    Check 'wait --below without --idle is an error' ((AP -t local wait --below 5 --timeout 1s).Code -notin 0, 124) ''

    # The managed heap, against a process whose objects are known: 321 kept in a static list, 500 dropped.
    $heapReady = Join-Path $work 'heap-ready.txt'
    $heapScript = "Add-Type 'public class ApVerifyLeak { public byte[] Data = new byte[1000]; } public static class ApVerifyHolder { public static System.Collections.Generic.List<ApVerifyLeak> Kept = new System.Collections.Generic.List<ApVerifyLeak>(); }'; 1..321 | ForEach-Object { [ApVerifyHolder]::Kept.Add((New-Object ApVerifyLeak)) }; 1..500 | ForEach-Object { `$null = New-Object ApVerifyLeak }; 'ready' | Set-Content '$heapReady'; Start-Sleep 600"
    $managed = Start-Process powershell -ArgumentList '-NoProfile', '-Command', $heapScript -WindowStyle Hidden -PassThru
    1..60 | ForEach-Object { if (-not (Test-Path $heapReady)) { Start-Sleep -Milliseconds 500 } }
    $r = AP -t local heap $managed.Id --type ApVerifyLeak --live
    Check 'heap --live counts exactly the objects that are still referenced (321)' ($r.Code -eq 0 -and $r.Out -match '(?m)^\s*321\s+\S+\s+ApVerifyLeak\s*$') $r.Out
    $r = AP -t local heap $managed.Id --type ApVerifyLeak
    $all = if ($r.Out -match '(?m)^\s*(\d+)\s+\S+\s+ApVerifyLeak\s*$') { [int]$Matches[1] } else { -1 }
    Check 'heap without --live also counts garbage (321..821) and says so' ($all -ge 321 -and $all -le 821 -and $r.Out -match 'garbage not yet collected included') "$all / $($r.Out)"
    $r = AP -t local heap $managed.Id --why ApVerifyLeak
    Check 'heap --why: the chain from the root, through the list that holds it, to the object' ($r.Code -eq 0 -and $r.Out -match '(?m)^root: ' -and $r.Out -match 'List<ApVerifyLeak> \(\d+B\)\s+-> \._items' -and ($r.Out -split "`n")[-1] -match '^\s+ApVerifyLeak \(\d+B\)$') $r.Out
    $r = AP -t local heap $managed.Id --why NoSuchTypeForVerify
    Check 'heap --why for a type nothing reachable has: says so, with how much was looked at' ($r.Out -match "no reachable object whose type contains 'NoSuchTypeForVerify' \([\d,]+ reachable objects looked at\)") $r.Out
    Stop-Process -Id $managed.Id -Force
    $native = Start-Process ping -ArgumentList '-n', '60', '127.0.0.1' -WindowStyle Hidden -PassThru; Start-Sleep 1
    $r = AP -t local heap $native.Id
    Check 'heap: a process without .NET is an error, not an empty heap' ($r.Code -ne 0 -and $r.Out -match 'no \.NET runtime is loaded') "$($r.Code) $($r.Out)"
    Stop-Process -Id $native.Id -Force
    $ps32 = "$env:WINDIR\SysWOW64\WindowsPowerShell\v1.0\powershell.exe"
    if (Test-Path $ps32) {
        $x86 = Start-Process $ps32 -ArgumentList '-NoProfile', '-Command', 'Start-Sleep 60' -WindowStyle Hidden -PassThru; Start-Sleep 2
        $r = AP -t local heap $x86.Id
        Check 'heap: a 32-bit process is refused with the reason, nothing is guessed' ($r.Code -ne 0 -and $r.Out -match 'is a 32-bit process') "$($r.Code) $($r.Out)"
        Stop-Process -Id $x86.Id -Force
    }

    # A click must not land outside the desktop, and the coordinates `ui` prints can be passed as they are.
    $r = AP -t local click 999999 999999 --screen
    Check 'click: a point outside the desktop is refused, not clicked' ($r.Code -ne 0 -and $r.Out -match 'is outside the desktop' -and $r.Out -match 'nothing was done') "$($r.Code) $($r.Out)"
    $r = AP -t local click '@999999,999999'
    Check 'click @x,y is read as screen coordinates' ($r.Code -ne 0 -and $r.Out -match '999999,999999 is outside the desktop' -and $r.Out -match 'Screen coordinates were given') "$($r.Code) $($r.Out)"
    $r = AP -t local dump $helper.Id --keep-remote
    $kept = if ($r.Out -match 'kept on the target: (.+\.dmp)') { $Matches[1].Trim() } else { $null }
    Check 'dump --keep-remote leaves the dump on the target and prints where' ($r.Code -eq 0 -and $kept -and (Test-Path $kept) -and (Get-Item $kept).Length -gt 10000) $r.Out
    Push-Location "$env:WINDIR\System32"
    $r = AP -t local dump $helper.Id --keep-remote
    Pop-Location
    if ($r.Code -eq 0) { Write-Host '  skipped: this account may write into System32, so there is no refused folder to test with.' -ForegroundColor Yellow; if ($r.Out -match 'kept on the target: (.+\.dmp)') { Remove-Item $Matches[1].Trim() -Force -ErrorAction SilentlyContinue } }
    else { Check 'dump into a folder that cannot be written to: a plain reason, nothing dumped' ($r.Out -match '^dump: could not write .+Nothing was dumped' -and $r.Out -notmatch 'Exception') "$($r.Code) $($r.Out)" }
    if ($kept) { Remove-Item $kept -Force -ErrorAction SilentlyContinue }
    $r = AP -t local windows --class
    Check 'windows --class adds the class of each window' ($r.Out -match '(?m)^HWND\s+PID\s+PROCESS\s+POSITION\s+STATE\s+CLASS\s+TITLE') ($r.Out -split "`n" | Select-Object -First 1)

    # A question from Windows itself (here: a credential prompt) must show up in `windows`, `triage` and after `exec`.
    # It appears on the screen for a few seconds, so it is left out on a PC someone is using.
    if (-not $SkipInput) {
        $promptScript = Join-Path $work 'prompt.ps1'
        @'
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class CredPrompt {
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] struct INFO { public int cbSize; public IntPtr hwndParent; public string message; public string caption; public IntPtr banner; }
  [DllImport("credui.dll", CharSet=CharSet.Unicode)] static extern int CredUIPromptForWindowsCredentials(ref INFO info, int authError, ref uint package, IntPtr inAuth, uint inSize, out IntPtr outAuth, out uint outSize, ref bool save, int flags);
  public static int Show() { var i = new INFO(); i.cbSize = Marshal.SizeOf(typeof(INFO)); i.caption = "agentprobe verify"; i.message = "A test prompt. It closes by itself.";
    uint pkg = 0; IntPtr o; uint n; bool save = false; return CredUIPromptForWindowsCredentials(ref i, 0, ref pkg, IntPtr.Zero, 0, out o, out n, ref save, 0x1); }
}
"@
[CredPrompt]::Show()
'@ | Set-Content $promptScript -Encoding UTF8
        $prompter = Start-Process powershell -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$promptScript`"" -WindowStyle Hidden -PassThru
        $broker = $null; 1..20 | ForEach-Object { if (-not $broker) { Start-Sleep -Milliseconds 500; $broker = Get-Process CredentialUIBroker -ErrorAction SilentlyContinue | Select-Object -First 1 } }
        if (-not $broker) {
            Write-Host '  skipped: this session cannot show a Windows credential prompt, so there is no system prompt to detect.' -ForegroundColor Yellow
        } else {
            Start-Sleep 1
            $w = (AP -t local windows).Out; $tr = (AP -t local triage).Out; $ex = (AP -t local exec 'Write-Output ran').Out
            Check 'windows marks a prompt from Windows itself as SYSTEM PROMPT' ($w -match "(?m)\s$($broker.Id)\s+CredentialUIBroker\.exe\s.*SYSTEM PROMPT") $w
            Check 'triage and exec report the waiting system prompt' ($tr -match "(?m)^prompt\s.*CredentialUIBroker\.exe pid $($broker.Id)" -and $ex -match 'ran' -and $ex -match 'a system dialog is waiting') "$tr`n$ex"
        }
        Stop-Process -Id $prompter.Id -Force -ErrorAction SilentlyContinue; Start-Sleep 1
        Get-Process CredentialUIBroker -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue; Start-Sleep 1
        Check 'windows: no SYSTEM PROMPT once it is gone' ((AP -t local windows).Out -notmatch 'SYSTEM PROMPT') ''
    }

    # A VPN tunnel carries no broadcast, so a developer PC across one is only found by probing each address.
    # Every subnet of up to /22 that Windows reports on a connected adapter must be searched that way, with the right count.
    $r = AP share --dry-run --lang en
    $connected = @(Get-NetIPInterface -AddressFamily IPv4 -ConnectionState Connected | ForEach-Object { $_.ifIndex })
    $expect = @{}
    Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $connected -contains $_.InterfaceIndex -and $_.IPAddress -notlike '127.*' -and $_.PrefixLength -ge 22 -and $_.PrefixLength -le 31 } | ForEach-Object {
        $o = $_.IPAddress.Split('.') | ForEach-Object { [uint64]$_ }
        $size = [uint64][Math]::Pow(2, 32 - $_.PrefixLength)
        $net = [uint64]([Math]::Floor((($o[0] * 16777216) + ($o[1] * 65536) + ($o[2] * 256) + $o[3]) / $size) * $size)
        $cidr = "$([Math]::Floor($net / 16777216)).$([Math]::Floor($net / 65536) % 256).$([Math]::Floor($net / 256) % 256).$($net % 256)/$($_.PrefixLength)"
        $expect[$cidr] = if ($_.PrefixLength -eq 31) { 2 } else { $size - 2 }
    }
    $got = @{}
    $r.Out -split "`n" | ForEach-Object { if ($_ -match '^\s+each address\s+(\S+)\s+(\d+) addresses') { $got[$Matches[1]] = [int]$Matches[2] } }
    $wrong = @($got.Keys | Where-Object { $expect[$_] -ne $got[$_] })
    $total = ($expect.Values | Measure-Object -Sum).Sum
    $missing = if ($total -le 4096) { @($expect.Keys | Where-Object { -not $got.ContainsKey($_) }) } else { @() }   # above the cap some are left out, by design
    Check 'share --dry-run: each small subnet Windows reports is searched address by address' ($r.Code -eq 0 -and $wrong.Count -eq 0 -and $missing.Count -eq 0 -and ($got.Values | Measure-Object -Sum).Sum -le 4096) "wrong: $($wrong -join ' ')  missing: $($missing -join ' ')`n$($r.Out)"
    Check 'share --dry-run: a name that does not resolve is reported, not dropped' ((AP share --dry-run --lang en name.invalid 192.0.2.7).Out -match '(?m)^\s+NOT SEARCHED\s+name\.invalid' -and (AP share --dry-run --lang en name.invalid 192.0.2.7).Out -match '(?m)^\s+address\s+192\.0\.2\.7:\d+') ''

    $fake = Join-Path $work 'dotnetapp'; New-Item -ItemType Directory $fake | Out-Null
    Copy-Item "$env:WINDIR\System32\where.exe" (Join-Path $fake 'App.exe')
    '{"runtimeOptions":{"framework":{"name":"Microsoft.NETCore.App","version":"99.0.0"}}}' | Set-Content (Join-Path $fake 'App.runtimeconfig.json')
    $r = AP -t local deps (Join-Path $fake 'App.exe')
    Check 'deps: a .NET app host whose runtime is missing is reported' ($r.Code -eq 1 -and $r.Out -match 'NO RUNTIME\s+Microsoft\.NETCore\.App 99') $r.Out

    # The MCP server speaks JSON-RPC on stdio: ask it what it offers.
    $mcp = New-Object System.Diagnostics.ProcessStartInfo; $mcp.FileName = $Exe; $mcp.Arguments = 'mcp'; $mcp.UseShellExecute = $false; $mcp.CreateNoWindow = $true
    $mcp.RedirectStandardInput = $true; $mcp.RedirectStandardOutput = $true; $mcp.StandardOutputEncoding = [Text.Encoding]::UTF8
    $mp = [System.Diagnostics.Process]::Start($mcp)
    $mp.StandardInput.WriteLine('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"verify","version":"1"}}}')
    $mp.StandardInput.WriteLine('{"jsonrpc":"2.0","id":2,"method":"tools/list"}')
    $mp.StandardInput.Close()
    $answers = $mp.StandardOutput.ReadToEnd() -split "`n" | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json }
    $mp.WaitForExit()
    $tools = ($answers | Where-Object { $_.id -eq 2 }).result.tools.name
    Check 'mcp: initialize answers with instructions' ((($answers | Where-Object { $_.id -eq 1 }).result.instructions) -match 'agentprobe') ''
    Check 'mcp: offers the four tools' (($tools -join ',') -eq 'list_targets,run,screenshot,guide') ($tools -join ',')

    $r = AP -t local deps $Exe
    Check 'deps: a healthy exe has no problems' ($r.Code -eq 0 -and $r.Out -match '^ok:|\nok:') $r.Out
    $broken = Join-Path $work 'broken'; New-Item -ItemType Directory $broken | Out-Null
    Copy-Item "$env:WINDIR\System32\where.exe" (Join-Path $broken 'tool.exe')
    $bytes = [IO.File]::ReadAllBytes((Join-Path $broken 'tool.exe')); $ascii = [Text.Encoding]::ASCII.GetString($bytes)
    $i = $ascii.IndexOf('KERNEL32.dll', [StringComparison]::OrdinalIgnoreCase)
    if ($i -gt 0) {
        [Text.Encoding]::ASCII.GetBytes('NOSUCH99.dll').CopyTo($bytes, $i); [IO.File]::WriteAllBytes((Join-Path $broken 'tool.exe'), $bytes)
        $r = AP -t local deps (Join-Path $broken 'tool.exe')
        Check 'deps: reports the missing DLL, exit 1' ($r.Code -eq 1 -and $r.Out -match 'MISSING\s+NOSUCH99\.dll') $r.Out
    } else { Check 'deps: could not prepare the broken exe' $false '' }

    # ------------------------------------------------------------------ exec (needs a session) is below; local helpers here
    Write-Host "`nmonitoring with a deliberate leak"
    $leakScript = '$l = New-Object System.Collections.ArrayList; while ($true) { [void]$l.Add((New-Object byte[] (4MB))); Start-Sleep -Milliseconds 400 }'
    $leak = Start-Process powershell -ArgumentList '-NoProfile', '-Command', $leakScript -WindowStyle Hidden -PassThru
    Start-Sleep 2
    $v = AP -t local vmmap $leak.Id --interval 4s
    Check 'vmmap: private data grows for the leaking process' ($v.Out -match 'private data\s+\S+\s+\+\d') $v.Out
    $v2 = AP -t local vmmap $helper.Id --interval 2s
    Check 'vmmap: idle process shows no private growth' ($v2.Out -notmatch 'private data\s+\S+\s+\+\d+(\.\d+)?[MG]') $v2.Out
    $d = AP -t local dump $leak.Id -o $work
    $dmp = Get-ChildItem $work -Filter *.dmp | Select-Object -First 1
    Check 'dump writes a valid minidump' ($d.Code -eq 0 -and $dmp -and ([Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($dmp.FullName), 0, 4) -eq 'MDMP')) $d.Out
    $w = AP -t local wait --exit $leak.Id --timeout 2s
    Check 'wait --exit times out with 124 while it runs' ($w.Code -eq 124) $w.Out
    Stop-Process -Id $leak.Id -Force
    $w = AP -t local wait --exit $leak.Id --timeout 5s
    Check 'wait --exit succeeds after it ends' ($w.Code -eq 0) $w.Out

    # ------------------------------------------------------------------ desktop
    Write-Host "`ndesktop and UI automation (a small test window appears)"
    $clickFile = Join-Path $work 'clicked.txt'
    $formScript = @"
Add-Type -AssemblyName System.Windows.Forms, PresentationFramework, PresentationCore, WindowsBase
Add-Type -ReferencedAssemblies System.Windows.Forms 'namespace ApVerify.Views { public class EditorForm : System.Windows.Forms.Form { } public class Cache<T> { public class Node { } } public static class Holder { public static object[] Nodes = new object[] { new Cache<int>.Node(), new Cache<int>.Node(), new Cache<int>.Node(), new Cache<int>.Node(), new Cache<int>.Node() }; } }'
`$nodes = [ApVerify.Views.Holder]::Nodes.Length
[Windows.Forms.Application]::EnableVisualStyles()
`$neverShown = 1..3 | ForEach-Object { New-Object ApVerify.Views.EditorForm }
`$wpfNeverShown = 1..2 | ForEach-Object { New-Object System.Windows.Window }
`$wpfShown = New-Object System.Windows.Window; `$wpfShown.Title = 'ApWpf'; `$wpfShown.Width = 160; `$wpfShown.Height = 80; `$wpfShown.Left = 700; `$wpfShown.Top = 520; `$wpfShown.ShowInTaskbar = `$false; `$wpfShown.ShowActivated = `$false; `$wpfShown.Show()
`$formClosed = New-Object ApVerify.Views.EditorForm; `$formClosed.ShowInTaskbar = `$false; `$formClosed.StartPosition = 'Manual'; `$formClosed.Left = 700; `$formClosed.Top = 620; `$formClosed.Width = 120; `$formClosed.Height = 60; `$formClosed.Show(); `$formClosed.Close()
`$wpfClosed = New-Object System.Windows.Window; `$wpfClosed.Width = 120; `$wpfClosed.Height = 60; `$wpfClosed.Left = 700; `$wpfClosed.Top = 620; `$wpfClosed.ShowInTaskbar = `$false; `$wpfClosed.ShowActivated = `$false; `$wpfClosed.Show(); `$wpfClosed.Close()
`$f = New-Object Windows.Forms.Form; `$f.Text = 'AP Verify Window'; `$f.Width = 440; `$f.Height = 190; `$f.StartPosition = 'Manual'; `$f.Left = 160; `$f.Top = 160; `$f.TopMost = `$true
`$t = New-Object Windows.Forms.TextBox; `$t.Name = 'inputBox'; `$t.Left = 20; `$t.Top = 20; `$t.Width = 300
`$b = New-Object Windows.Forms.Button; `$b.Name = 'pressButton'; `$b.Text = 'Press me'; `$b.Left = 20; `$b.Top = 60; `$b.Width = 120
`$b.Add_Click({ [IO.File]::WriteAllText('$clickFile', 'clicked:' + `$t.Text) })
`$d = New-Object Windows.Forms.Form; `$d.Text = 'AP Verify Dialog'; `$d.Width = 260; `$d.Height = 120; `$d.StartPosition = 'Manual'; `$d.Left = 660; `$d.Top = 160
`$c = New-Object Windows.Forms.Button; `$c.Name = 'closeDialog'; `$c.Text = 'Close dialog'; `$c.Left = 20; `$c.Top = 20; `$c.Width = 120; `$c.DialogResult = 'OK'; `$d.Controls.Add(`$c)
`$o = New-Object Windows.Forms.Button; `$o.Name = 'openDialog'; `$o.Text = 'Open dialog'; `$o.Left = 160; `$o.Top = 60; `$o.Width = 120
`$o.Add_Click({ [void]`$f.BeginInvoke([Action]{ [void]`$d.ShowDialog(`$f) }) })
`$f.Controls.Add(`$t); `$f.Controls.Add(`$b); `$f.Controls.Add(`$o); [Windows.Forms.Application]::Run(`$f)
"@
    $form = Start-Process powershell -ArgumentList '-NoProfile', '-STA', '-WindowStyle', 'Hidden', '-EncodedCommand', ([Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($formScript))) -PassThru
    $w = AP -t local wait --window 'AP Verify Window' --timeout 20s
    Check 'wait --window sees the test window' ($w.Code -eq 0) $w.Out
    $win = AP -t local windows 'AP Verify'
    Check 'windows lists it with its pid' ($win.Out -match "\s$($form.Id)\s+powershell\.exe") $win.Out

    # Window objects on the managed heap. That process holds, per type (count / on screen / closed):
    #   EditorForm (derived from Form)  4 / 0 / 1   three never shown, one shown and closed again
    #   Form                            2 / 1 / 0   the form on screen, and its dialog that is not open
    #   WPF Window                      4 / 1 / 1   two never shown, one on screen, one shown and closed again
    $r = AP -t local heap $form.Id --windows
    Check 'heap --windows: window objects per type, how many are shown and how many were closed' ($r.Code -eq 0 -and $r.Out -match '(?m)^\s*4\s+0\s+1\s+ApVerify\.Views\.EditorForm\s*$' -and $r.Out -match '(?m)^\s*2\s+1\s+0\s+System\.Windows\.Forms\.Form\s*$' -and $r.Out -match '(?m)^\s*4\s+1\s+1\s+System\.Windows\.Window\s*$' -and $r.Out -match '(?m)^CLOSED: ') $r.Out
    $r = AP -t local heap $form.Id --type Form --live --top 300
    Check 'heap --type looks at the type''s own name, not at its namespace' ($r.Out -match '(?m)^\s*4\s+\S+\s+ApVerify\.Views\.EditorForm\s*$' -and $r.Out -match '(?m)^\s*2\s+\S+\s+System\.Windows\.Forms\.Form\s*$' -and $r.Out -notmatch '(?m)System\.Windows\.Forms\.(Button|TextBox)\s*$') $r.Out
    $r = AP -t local heap $form.Id --type Node --ns ApVerify --live
    Check 'heap --type finds a type nested in a generic type by its own name' ($r.Out -match '(?m)^\s*5\s+\S+\s+ApVerify\.Views\.Cache<System\.Int32>\+Node\s*$' -and @($r.Out -split "`n").Count -eq 3) $r.Out
    $r = AP -t local heap $form.Id --type DisplayClass --live --top 1
    Check 'heap --type finds compiler-made types (their names begin with <)' ($r.Code -eq 0 -and $r.Out -match '(?m)^\s*[\d,]+\s+\S+\s+\S*<>c__DisplayClass') $r.Out
    $r = AP -t local heap $form.Id --type Forms.Button --live
    Check 'heap --type with a dot looks in the full name' ($r.Out -match '(?m)^\s*3\s+\S+\s+System\.Windows\.Forms\.Button\s*$') $r.Out
    $r = AP -t local heap $form.Id --ns ApVerify.Views --live
    Check 'heap --ns keeps the types of one namespace' ($r.Out -match '(?m)^\s*4\s+\S+\s+ApVerify\.Views\.EditorForm\s*$' -and $r.Out -match 'Cache<System\.Int32>\+Node' -and $r.Out -notmatch '(?m)\sSystem\.') $r.Out
    $r = AP -t local heap $form.Id --type 'ApVerify.*Form' --live
    Check 'heap --type with * is a pattern for the whole name' ($r.Out -match '(?m)^\s*4\s+\S+\s+ApVerify\.Views\.EditorForm\s*$' -and @($r.Out -split "`n").Count -eq 3) $r.Out
    $r = AP -t local heap $form.Id --type ApVerify --live
    Check 'heap --type that is only in a namespace: says so and names --ns' ($r.Out -match 'no reachable object of a type matching --type ApVerify' -and $r.Out -match "'ApVerify' is in \d+ type names, but not in the type's own name" -and $r.Out -match '--ns ApVerify') $r.Out
    $hinted = (AP -t local heap $form.Id --type '*ApVerify*' --live).Out
    Check 'heap: the pattern that hint suggests does list those types' ($hinted -match 'ApVerify\.Views\.EditorForm' -and $hinted -match 'Cache<System\.Int32>\+Node') $hinted

    $tree = AP -t local ui 'AP Verify Window'
    Check 'ui: tree shows the button by name' ($tree.Out -match 'Button "Press me" #pressButton') $tree.Out
    Check 'ui: tree shows the edit field by id' ($tree.Out -match 'Edit.*#inputBox') $tree.Out
    $s = AP -t local ui set '#inputBox' 'hello ui' --window 'AP Verify Window'
    Check 'ui set succeeds' ($s.Code -eq 0) $s.Out
    Check 'ui get reads the value back' ((AP -t local ui get '#inputBox' --window 'AP Verify Window').Out -match '=hello ui') ''
    $c = AP -t local ui click 'Press me' --window 'AP Verify Window'
    Start-Sleep -Milliseconds 500
    Check 'ui click invoked the button (app saw the text)' ((Test-Path $clickFile) -and ((Get-Content $clickFile -Raw -Encoding UTF8) -eq 'clicked:hello ui')) "$($c.Out) / file: $(if (Test-Path $clickFile) { Get-Content $clickFile -Raw -Encoding UTF8 })"

    # raw input: focus, click by coordinates from the tree, select all, type, press the button with the keyboard
    if (-not $SkipInput) {
    Remove-Item $clickFile -ErrorAction SilentlyContinue
    [void](AP -t local focus 'AP Verify Window'); Start-Sleep -Milliseconds 300
    $edit = (AP -t local ui 'AP Verify Window' --grep inputBox).Out
    if ($edit -match '@(\d+),(\d+)') {
        $ex = [int]$Matches[1]; $ey = [int]$Matches[2]
        # --hover moves the pointer and presses nothing
        Add-Type -AssemblyName System.Windows.Forms
        [void](AP -t local click ($ex + 15) ($ey + 2) --hover --screen); Start-Sleep -Milliseconds 200
        $at = [System.Windows.Forms.Cursor]::Position
        Check 'click --hover moves the pointer there without clicking' ([Math]::Abs($at.X - ($ex + 15)) -le 1 -and [Math]::Abs($at.Y - ($ey + 2)) -le 1 -and -not (Test-Path $clickFile)) "pointer at $($at.X),$($at.Y), asked for $($ex + 15),$($ey + 2)"
        [void](AP -t local click $ex $ey --screen); Start-Sleep -Milliseconds 200
        [void](AP -t local key ctrl+a); [void](AP -t local type 'typed 한글 a\nb'); Start-Sleep -Milliseconds 300
        Check 'click + type put the text in the field, backslashes untouched' ((AP -t local ui get '#inputBox' --window 'AP Verify Window').Out -match '=typed 한글 a\\nb') (AP -t local ui get '#inputBox' --window 'AP Verify Window').Out
        [void](AP -t local key tab); [void](AP -t local key space); Start-Sleep -Milliseconds 500
        Check 'key presses reached the app (button pressed with the keyboard)' ((Test-Path $clickFile) -and ((Get-Content $clickFile -Raw -Encoding UTF8) -eq 'clicked:typed 한글 a\nb')) "file: $(if (Test-Path $clickFile) { Get-Content $clickFile -Raw -Encoding UTF8 })"
    } else { Check 'ui: edit field position' $false $edit }
    }

    # A modal dialog disables the window it belongs to: `windows` says so, and a click on that window names it.
    Remove-Item $clickFile -ErrorAction SilentlyContinue
    [void](AP -t local ui click 'Open dialog' --window 'AP Verify Window')
    $dw = AP -t local wait --window 'AP Verify Dialog' --timeout 15s
    $win = (AP -t local windows 'AP Verify').Out
    Check 'windows: the owner of an open modal dialog is marked disabled, the dialog is not' ($dw.Code -eq 0 -and $win -match '(?m)\sdisabled\s+AP Verify Window\s*$' -and $win -notmatch '(?m)disabled\s+AP Verify Dialog' -and $win -match '(?m)^disabled = ') $win
    if (-not $SkipInput) {
        $press = (AP -t local ui 'AP Verify Window' --grep pressButton).Out
        if ($press -match '@(\d+),(\d+)') {
            $r = AP -t local click "@$($Matches[1]),$($Matches[2])"
            Start-Sleep -Milliseconds 400
            Check 'click on a disabled window: says that it is disabled, and the app did not get the click' ($r.Out -match 'note: that point is on "AP Verify Window" \(powershell\.exe pid \d+\), which is disabled' -and -not (Test-Path $clickFile)) "$($r.Out) / clicked: $(Test-Path $clickFile)"
        } else { Check 'ui: button position while the dialog is open' $false $press }
    }
    [void](AP -t local ui click 'Close dialog' --window 'AP Verify Dialog')
    Start-Sleep -Milliseconds 700
    $win = (AP -t local windows 'AP Verify').Out
    Check 'windows: no longer disabled once the dialog is closed' ($win -match 'AP Verify Window' -and $win -notmatch 'disabled' -and $win -notmatch 'AP Verify Dialog') $win

    $shot = Join-Path $work 'shot.png'
    $sr = AP -t local screenshot --window 'AP Verify Window' -o $shot
    $okPng = (Test-Path $shot) -and (([IO.File]::ReadAllBytes($shot))[1..3] -join ',') -eq '80,78,71'
    Check 'screenshot of one window is a PNG' $okPng $sr.Out
    if ($okPng -and $sr.Out -match '(\d+)x(\d+) PNG') {
        $b = [IO.File]::ReadAllBytes($shot); $pw = [BitConverter]::ToUInt32($b[19..16], 0); $ph = [BitConverter]::ToUInt32($b[23..20], 0)
        Check 'screenshot size in the file matches what it reports' ($pw -eq [int]$Matches[1] -and $ph -eq [int]$Matches[2]) "file ${pw}x${ph}, reported $($Matches[1])x$($Matches[2])"
    }
    Stop-Process -Id $form.Id -Force
    $w = AP -t local wait --no-window 'AP Verify Window' --timeout 5s
    Check 'wait --no-window after it closes' ($w.Code -eq 0) $w.Out

    # ------------------------------------------------------------------ real session
    if ($Session) {
        Write-Host "`nremote path (daemon + a local target)"
        $existing = (AP list).Out
        if ($existing -notmatch 'No target PC is connected') {
            Write-Host '  skipped: targets are already connected to this PC; not touching a live session.' -ForegroundColor Yellow
        } else {
            # What the command line says itself is Korean for a person on a Korean Windows, and English for an agent
            # (piped output) whatever the language of the PC.
            $env:AGENTPROBE_LANG = 'ko'
            $piped = (AP list).Out
            $env:AGENTPROBE_PERSON = '1'
            $person = (AP list).Out
            Remove-Item Env:AGENTPROBE_PERSON, Env:AGENTPROBE_LANG
            Check 'list: English for an agent on a Korean PC, Korean for a person' ($piped -match '^No target PC is connected' -and $person -match '^연결된 대상 PC가 없습니다' -and $person -match [regex]::Escape($env:COMPUTERNAME)) "piped: $(($piped -split "`n")[0])  person: $(($person -split "`n")[0])"

            # --trust with this PC's own key, not --yes: another developer PC may be in reach (same LAN, a VPN),
            # and the local target must connect to this one.
            $myKey = [regex]::Match($existing, 'key ([0-9A-F]{4}(-[0-9A-F]{4}){3})').Groups[1].Value
            $share = Start-Process $Exe -ArgumentList 'share', '--trust', $myKey, '--name', 'verify' -WindowStyle Minimized -PassThru
            $up = $false; 1..30 | ForEach-Object { if (-not $up) { Start-Sleep -Milliseconds 700; if ((AP list).Out -match '(?m)^verify\s') { $up = $true } } }
            Check 'a local target connects by discovery' $up (AP list).Out
            if ($up) {
                $dry = AP share --dry-run --lang en
                Check 'share --dry-run: the developer PC that answers is listed with its key, and nothing is shared' ($dry.Code -eq 0 -and $dry.Out -match "(?m)^\s+$([regex]::Escape($env:COMPUTERNAME))\s+\(user .+\)\s+key $myKey\s+at [\d., ]+$" -and @((AP list).Out -split "`n" | Where-Object { $_ -match '\sidle\s|\sbusy' }).Count -eq 1) $dry.Out

                # The start screen as text (piped): the link, the MCP command with this exe's path, the connected PC.
                $homeText = (AP home).Out
                Check 'home: prints the dashboard link, the MCP command for this exe and the connected PC' ($homeText -match 'http://127\.0\.0\.1:\d+/\?k=[0-9A-F]{16,}' -and $homeText.Contains("claude mcp add agentprobe -- `"$Exe`" mcp") -and $homeText -match '(?m):\s.*\bverify\b.*$') $homeText
                $bare = AP
                Check 'no arguments, piped: prints the command list instead of opening a screen' ($bare.Code -eq 0 -and $bare.Out -match 'ON THE PC TO TEST') (($bare.Out -split "`n" | Select-Object -First 2) -join ' / ')

                # The leak verdict needs two minutes of evidence: start watching now and judge at the end of this section.
                $leak2 = Start-Process powershell -ArgumentList '-NoProfile', '-Command', $leakScript -WindowStyle Hidden -PassThru
                Start-Sleep 2
                # A watched process that starts a child later, and an unrelated process that ends with a crash code.
                $watchParent = Start-Process powershell -ArgumentList '-NoProfile', '-Command', 'Start-Sleep 8; cmd /c ''ping -n 3 127.0.0.1 >nul & exit 3''; Start-Sleep 900' -WindowStyle Hidden -PassThru
                Start-Sleep 1
                [void](AP -t verify watch start $leak2.Id $helper.Id $watchParent.Id --interval 1s)
                $watchStarted = Get-Date
                Start-Sleep 2
                Start-Process cmd -ArgumentList '/c', 'ping -n 3 127.0.0.1 >nul & exit -1073741819' -WindowStyle Hidden

                $r = AP -t verify exec '[Console]::Out.Write("first`r`r`nsecond`r`nthird`n")'
                Check 'exec: lines ending in CR CR LF are kept' (($r.Out -split "`n") -join '|' -eq 'first|second|third') ($r.Out -replace "`n", '|')
                $r = AP -t verify read $big --head 120000
                $savedBig = if ($r.Out -match 'Full output \((\d+) lines\): (.+?\.txt)') { $Matches[2] } else { $null }
                Check 'a 7 MB answer arrives complete (no hang, nothing lost)' ($r.Code -eq 0 -and $savedBig -and (Get-Content $savedBig).Count -eq 120000) "exit $($r.Code), saved $savedBig"
                $r = AP -t verify events --log NoSuchLogForVerify
                Check 'events: an unreadable log is an error, not "no events"' ($r.Code -eq 1 -and $r.Out -match 'could not read NoSuchLogForVerify') "$($r.Code) $($r.Out)"
                $r = AP -t verify dump $helper.Id --full -o $work
                Check 'dump --full reaches the tool (a full dump is written)' ($r.Out -match '(?m)^full dump of') $r.Out

                $r = AP -t verify exec 'Write-Output "한글 ok"; exit 7'
                Check 'exec: unicode output and exit code' ($r.Out -match '한글 ok' -and $r.Code -eq 7) "$($r.Code) $($r.Out)"
                $r = AP -t verify exec --shell cmd 'echo cmd-ok & exit /b 3'
                Check 'exec --shell cmd exit code' ($r.Out -match 'cmd-ok' -and $r.Code -eq 3) "$($r.Code) $($r.Out)"
                $r = AP -t verify exec 'Get-Item C:\no-such-path-for-verify'
                Check 'exec: PowerShell error is plain text, not CLIXML' ($r.Out -notmatch 'CLIXML|<Objs' -and $r.Out -match 'no-such-path-for-verify' -and $r.Code -ne 0) $r.Out
                $r = AP -t verify exec --timeout 2s 'Start-Sleep 30'
                Check 'exec --timeout returns 124' ($r.Code -eq 124) "$($r.Code) $($r.Out)"
                $r = AP -t verify exec '1..1000 | ForEach-Object { "row $_" }'
                $saved = if ($r.Out -match 'Full output \((\d+) lines\): (.+?\.txt)') { $Matches[2] } else { $null }
                Check 'long output is cut and says so' ($r.Out -match 'lines omitted' -and $r.Out -match 'row 1\b' -and $r.Out -match 'row 1000') ''
                Check 'the saved full output has every line' ($saved -and (Get-Content $saved).Count -eq 1000) "saved: $saved"
                $r = AP -t verify exec --full '1..1000 | ForEach-Object { "row $_" }'
                Check 'exec --full prints everything' (($r.Out -split "`n" | Where-Object { $_ -match '^row \d+$' }).Count -eq 1000) ''

                $bin = Join-Path $work 'random.bin'; $rnd = New-Object byte[] (3MB); (New-Object Random 42).NextBytes($rnd); [IO.File]::WriteAllBytes($bin, $rnd)
                [void](AP -t verify push $bin 'roundtrip.bin')
                $back = Join-Path $work 'back.bin'; [void](AP -t verify pull 'roundtrip.bin' $back)
                Check 'push + pull returns identical bytes' ((Test-Path $back) -and (Get-FileHash $back).Hash -eq (Get-FileHash $bin).Hash) ''
                $tree = Join-Path $work 'tree'; New-Item -ItemType Directory "$tree\sub\deep" -Force | Out-Null; 'a' | Set-Content "$tree\a.txt"; '한글' | Set-Content "$tree\sub\deep\k.txt" -Encoding UTF8
                $p1 = AP -t verify push $tree 'treecopy'; $treeBack = Join-Path $work 'treeback'; [void](AP -t verify pull 'treecopy' $treeBack)
                Check 'folder push + pull keeps the structure' ((Test-Path "$treeBack\sub\deep\k.txt") -and (Get-Content "$treeBack\sub\deep\k.txt" -Encoding UTF8) -eq '한글') ''
                $more = Join-Path $work 'more'; New-Item -ItemType Directory $more | Out-Null; 'b' | Set-Content "$more\b.txt"
                $p2 = AP -t verify push $more 'treecopy'
                Check 'folder push says how many files were sent, and separately what a merged folder now holds' ($p1.Out -match '\(2 files, ' -and $p2.Out -match '\(1 file sent, .*merged into the existing folder, which now holds 3 files\)') "$($p1.Out) / $($p2.Out)"

                # A script sent with -f is run as a file: its param block works and what follows the file name reaches it.
                $ps = Join-Path $work 'params.ps1'
                "param([int]`$Port = 1, [string]`$Name = 'none', [switch]`$Fast)`r`n`"port=`$Port name=`$Name fast=`$Fast file=`$(`$PSScriptRoot.Length -gt 0)`"`r`nexit 9" | Set-Content $ps -Encoding UTF8
                $r = AP -t verify exec -f $ps -Port 8080 -Name "two words, it's" -Fast
                Check 'exec -f: the script keeps its param block and receives its arguments' ($r.Out -match "port=8080 name=two words, it's fast=True file=True" -and $r.Code -eq 9) "$($r.Code) $($r.Out)"
                $r = AP -t verify exec --timeout 30s -f $ps
                Check 'exec -f without arguments: the defaults of the param block' ($r.Out -match 'port=1 name=none fast=False' -and $r.Code -eq 9) "$($r.Code) $($r.Out)"
                $r = AP -t verify exec -f $ps -Port:7 '-Fast:$false' -Name:x
                Check 'exec -f: -Name:value and -Switch:$false are passed as such' ($r.Out -match 'port=7 name=x fast=False') "$($r.Code) $($r.Out)"
                $ps2 = Join-Path $work 'laststep.ps1'; "Write-Output started`r`ncmd /c exit 5" | Set-Content $ps2 -Encoding UTF8
                $r = AP -t verify exec -f $ps2
                Check 'exec -f: a script whose last step fails returns that exit code, not 0' ($r.Code -eq 5 -and $r.Out -match 'started') "$($r.Code) $($r.Out)"

                # A GUI app is tracked as a job only when it is started itself, not through a shell.
                $gui = "$env:WINDIR\System32\winver.exe"   # a plain GUI program that every Windows has, server editions included
                $r = AP -t verify start --name gui --exe $gui
                $guiPid = if ($r.Out -match 'job \d+ started \(pid (\d+)\)') { [int]$Matches[1] } else { 0 }
                $guiProc = if ($guiPid) { Get-Process -Id $guiPid -ErrorAction SilentlyContinue }
                Check 'start --exe: the job is the app itself (its pid, still running)' ($guiProc -and $guiProc.ProcessName -eq 'winver' -and (AP -t verify jobs).Out -match "(?m)^\s*\d+\s+gui\s+$guiPid\s+running") "$($r.Out)`n$((AP -t verify jobs).Out)"
                [void](AP -t verify kill gui); Start-Sleep 1
                Check 'kill <job> closes an app started with --exe, and jobs shows its exit' (-not (Get-Process -Id $guiPid -ErrorAction SilentlyContinue) -and (AP -t verify jobs).Out -match "(?m)^\s*\d+\s+gui\s+$guiPid\s+exit ") (AP -t verify jobs).Out
                $r = AP -t verify start --name viashell "& '$gui'"
                Check 'start <command> that only launches a GUI app says the job is not the app' ($r.Out -match 'already ended' -and $r.Out -match 'start --exe') $r.Out
                [void](AP -t verify kill --name winver.exe)

                $grow = Join-Path $work 'grow.log'; 'first' | Set-Content $grow
                [void](AP -t verify read $grow --new)
                Add-Content $grow 'appended one'; Add-Content $grow 'appended two'
                Check 'read --new returns only appended lines' ((AP -t verify read $grow --new).Out.Trim() -eq "appended one`nappended two") ''
                Check 'read --new again says nothing new' ((AP -t verify read $grow --new).Out -match 'no new lines') ''

                [void](AP -t verify start --name napper 'Write-Output job-started; Start-Sleep 60')
                Start-Sleep 2
                Check 'logs returns job output' ((AP -t verify logs napper).Out -match 'job-started') ''
                Check 'logs again returns nothing new' ((AP -t verify logs napper).Out -match 'no new output') ''
                Check 'kill stops the job' ((AP -t verify kill napper).Code -eq 0 -and (AP -t verify wait --job napper --timeout 10s).Code -eq 0) ''

                $rep = (AP -t verify watch).Out
                Check 'watch: lists what a watched process started, and how that ended' ($rep -match "start cmd\.exe\(\d+\) by powershell\.exe\($($watchParent.Id)\)" -and $rep -match '(?m)exit\s+cmd\.exe\(\d+\) code 3\b') $rep
                Check 'watch: lists an unrelated process that ended with a crash code' ($rep -match '(?m)exit\s+cmd\.exe\(\d+\) code 0xC0000005') $rep
                Check 'watch: counts the starts and exits of unrelated processes instead of listing them' ($rep -match '\d+ start/exit events of unrelated processes left out: `watch --all-events` lists them' -and $rep -notmatch 'start agentprobe\.exe') $rep
                $all = (AP -t verify watch --all-events).Out
                Check 'watch --all-events: lists those too, from the same point in time' ($all -match '(?m)^all events since ' -and $all -match 'start agentprobe\.exe' -and $all -match "by powershell\.exe\($($watchParent.Id)\)" -and $all -notmatch 'left out') $all
                $leakLine = ($rep -split "`n" | Where-Object { $_ -match "powershell\.exe\($($leak2.Id)\)" } | Select-Object -First 1)
                if (((Get-Date) - $watchStarted).TotalSeconds -lt 110) {
                    Check 'watch: growth seen early is NOT called a leak yet' ("$leakLine" -match 'private .*\(\+\d.*?/min, growing; too early' -and $rep -notmatch 'LIKELY LEAK') $rep
                }
                $remaining = 128 - ((Get-Date) - $watchStarted).TotalSeconds
                if ($remaining -gt 0) { Write-Host "  ...  waiting $([int]$remaining)s so the leak check has two minutes of data"; Start-Sleep ([int]$remaining) }
                $rep = (AP -t verify watch).Out
                $leakLine = ($rep -split "`n" | Where-Object { $_ -match "powershell\.exe\($($leak2.Id)\)" } | Select-Object -First 1)
                $idleLine = ($rep -split "`n" | Where-Object { $_ -match "powershell\.exe\($($helper.Id)\)" } | Select-Object -First 1)
                Check 'watch: steady growth for two minutes is flagged, with a rate' ("$leakLine" -match 'private .*\(\+\d.*?/min, steady growth: LIKELY LEAK') $rep
                Check 'watch: the growth rate is about right (4 MB every 0.4 s = ~600 MB/min)' ("$leakLine" -match 'private .*\(\+(\d+)M/min' -and [int]$Matches[1] -gt 300 -and [int]$Matches[1] -lt 900) $leakLine
                Check 'watch: an idle process is not called a leak' ("$idleLine" -match 'powershell' -and "$idleLine" -notmatch 'LIKELY LEAK') $idleLine
                Stop-Process -Id $leak2.Id -Force; Start-Sleep 3
                $rep = (AP -t verify watch stop).Out
                Check 'watch: reports the exit of the watched process' ($rep -match "exit\s+powershell\.exe\($($leak2.Id)\)") $rep

                $h = (AP -t verify history --last 40).Out
                Check 'history lists what was run, with exit codes' ($h -match 'exit 7\s+exec Write-Output' -and $h -match 'exit 124') $h
                $shotR = Join-Path $work 'remote.png'; [void](AP -t verify screenshot --scale 0.25 -o $shotR)
                Check 'screenshot arrives over the session' ((Test-Path $shotR) -and (Get-Item $shotR).Length -gt 1000) ''
                # The firewall: "allow" is a rule for the length of the session. It needs administrator rights, which
                # this script has on the build machine and usually not on a developer's PC; both cases are checked.
                $elevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
                $fwApp = Join-Path $work 'fwapp.exe'; Copy-Item "$env:WINDIR\System32\where.exe" $fwApp
                $fwBlocked = Join-Path $work 'fwblocked.exe'; Copy-Item "$env:WINDIR\System32\where.exe" $fwBlocked
                Check 'firewall show: a program without a rule is said to have none' ((AP -t verify firewall show $fwApp).Out -match '^no inbound rule for ') ''
                $r = AP -t verify firewall allow $fwApp
                if (-not $elevated) {
                    Check 'firewall allow without administrator rights: refused with the reason, nothing changed' ($r.Code -ne 0 -and $r.Out -match 'needs an elevated target' -and -not (Get-NetFirewallApplicationFilter -Program $fwApp -ErrorAction SilentlyContinue)) "$($r.Code) $($r.Out)"
                } else {
                    $made = @(Get-NetFirewallApplicationFilter -Program $fwApp -ErrorAction SilentlyContinue | Get-NetFirewallRule)
                    Check 'firewall allow adds an enabled inbound allow rule for exactly that program' ($r.Code -eq 0 -and $made.Count -eq 1 -and "$($made[0].Action)" -eq 'Allow' -and "$($made[0].Direction)" -eq 'Inbound' -and "$($made[0].Enabled)" -eq 'True') "$($r.Code) $($r.Out)"
                    Check 'firewall show lists it' ((AP -t verify firewall show $fwApp).Out -match '(?m)^allow\s+yes\s+agentprobe session fwapp\.exe') (AP -t verify firewall show $fwApp).Out
                    [void](New-NetFirewallRule -DisplayName 'agentprobe verify block' -Direction Inbound -Program $fwBlocked -Action Block)
                    $r = AP -t verify firewall allow $fwBlocked
                    $block = Get-NetFirewallRule -DisplayName 'agentprobe verify block'
                    Check 'firewall allow switches off a block rule for the same program (a block would win)' ($r.Code -eq 0 -and $r.Out -match 'switched off the block rule' -and "$($block.Enabled)" -eq 'False') "$($r.Code) $($r.Out)"
                }
                Check 'firewall dismiss refuses a pid that shows no system dialog' ((AP -t verify firewall dismiss $PID).Code -ne 0 -and (AP -t verify firewall dismiss $PID).Out -match 'shows no system dialog') ''

                # Closing the start screen's window asks for this: it must never cut off a connected PC,
                # and with nothing connected it must leave no process behind (the exe stays locked otherwise).
                $k = AP stop --if-idle
                Check 'stop --if-idle keeps the background process while a PC is connected' ($k.Out -match 'kept running: 1 target PC is connected' -and (AP list).Out -match '(?m)^verify\s') $k.Out
                $e = AP -t verify end
                Check 'end closes the session and removes the workspace' ($e.Code -eq 0 -and $share.WaitForExit(8000)) $e.Out
                if ($elevated) {
                    $after = @(Get-NetFirewallApplicationFilter -Program $fwApp -ErrorAction SilentlyContinue)
                    $block = Get-NetFirewallRule -DisplayName 'agentprobe verify block' -ErrorAction SilentlyContinue
                    Check 'end takes the session''s firewall rule away and switches the block rule back on' ($after.Count -eq 0 -and $block -and "$($block.Enabled)" -eq 'True') "rules left: $($after.Count), block enabled: $($block.Enabled)"
                    Get-NetFirewallRule -DisplayName 'agentprobe verify block' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
                    Get-NetFirewallRule -DisplayName 'agentprobe session fw*' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
                }
                Start-Sleep 1
                $k = AP stop --if-idle
                Start-Sleep 2
                # Asked of this instance's own port: an agentprobe someone uses on this PC may run from the same exe.
                $left = @(Get-NetTCPConnection -LocalPort ([int]$env:AGENTPROBE_PORT) -State Listen -ErrorAction SilentlyContinue)
                Check 'stop --if-idle ends the background process once nothing is connected' ($k.Out -match 'daemon stopped' -and $left.Count -eq 0) "$($k.Out)  still listening: $($left.Count)"
            }
            if (-not $share.HasExited) { Stop-Process -Id $share.Id -Force }
            [void](AP stop)
        }

        # ------------------------------------------------------------------ updates
        # A local "release feed" offers a newer exe. A developer-PC copy and a target-PC copy of the exe run as a
        # third instance; neither the exe under test nor a real release is touched.
        Write-Host "`nupdates (a local release feed, copies of the exe)"
        $mainExe = $Exe; $mainInstance = $env:AGENTPROBE_INSTANCE; $mainPort = $env:AGENTPROBE_PORT
        $feedPort = 48673; $u = Join-Path $work 'upd'
        New-Item -ItemType Directory "$u\dev", "$u\target", "$u\feed" | Out-Null
        Copy-Item $mainExe "$u\dev\agentprobe.exe"; Copy-Item $mainExe "$u\target\agentprobe.exe"
        # "The release": the same program with a few bytes appended, so it is another file that still runs.
        $bytes = [IO.File]::ReadAllBytes($mainExe) + [byte[]](1..32)
        [IO.File]::WriteAllBytes("$u\feed\agentprobe.exe", $bytes)
        $released = (Get-FileHash "$u\feed\agentprobe.exe" -Algorithm SHA256).Hash
        $before = (Get-FileHash "$u\dev\agentprobe.exe" -Algorithm SHA256).Hash
        '{"tag_name":"v99.0.0","html_url":"https://example.invalid/notes","assets":[{"name":"agentprobe.exe","browser_download_url":"http://127.0.0.1:' + $feedPort + '/agentprobe.exe"},{"name":"agentprobe.exe.sha256","browser_download_url":"http://127.0.0.1:' + $feedPort + '/agentprobe.exe.sha256"}]}' | Set-Content "$u\feed\latest" -Encoding ascii
        $feed = Start-Process powershell -WindowStyle Hidden -PassThru -ArgumentList '-NoProfile', '-Command', "`$l = New-Object System.Net.HttpListener; `$l.Prefixes.Add('http://127.0.0.1:$feedPort/'); `$l.Start(); while (`$true) { `$c = `$l.GetContext(); `$f = Join-Path '$u\feed' (`$c.Request.Url.AbsolutePath.TrimStart('/')); if (Test-Path `$f -PathType Leaf) { `$b = [IO.File]::ReadAllBytes(`$f); `$c.Response.ContentLength64 = `$b.Length; `$c.Response.OutputStream.Write(`$b, 0, `$b.Length) } else { `$c.Response.StatusCode = 404 }; `$c.Response.Close() }"
        $share2 = $null
        try {
            $Exe = "$u\dev\agentprobe.exe"
            $env:AGENTPROBE_INSTANCE = 'verifyupd'; $env:AGENTPROBE_PORT = '48654'; $env:AGENTPROBE_UPDATE_URL = "http://127.0.0.1:$feedPort/latest"
            $feedUp = $false; 1..40 | ForEach-Object { if (-not $feedUp) { try { $feedUp = (Invoke-WebRequest "http://127.0.0.1:$feedPort/latest" -UseBasicParsing -TimeoutSec 2).StatusCode -eq 200 } catch { Start-Sleep -Milliseconds 250 } } }
            if (-not $feedUp) { throw 'the local release feed for the update checks did not start' }
            $key2 = [regex]::Match((AP list).Out, 'key ([0-9A-F]{4}(-[0-9A-F]{4}){3})').Groups[1].Value

            # Before anything asks for a check: the background process must find the release by itself.
            $noted = $false; 1..40 | ForEach-Object { if (-not $noted) { Start-Sleep -Milliseconds 500; $noted = (AP list).Out -match 'agentprobe 99\.0\.0 is available' } }
            Check 'the background process finds a newer release by itself, and list says so' $noted (AP list).Out
            $r = AP update --check
            Check 'update --check names the newer release and changes nothing' ($r.Code -eq 0 -and $r.Out -match 'agentprobe 99\.0\.0 is available' -and (Get-FileHash $Exe -Algorithm SHA256).Hash -eq $before) $r.Out
            Check 'update refuses an option it does not know' ((AP update --chek).Code -eq 2 -and (Get-FileHash $Exe -Algorithm SHA256).Hash -eq $before) ''

            # A download that does not match the published checksum must be refused, with the exe left as it was.
            ('0' * 64) + '  agentprobe.exe' | Set-Content "$u\feed\agentprobe.exe.sha256" -Encoding ascii
            $r = AP update
            Check 'update refuses a download that does not match the release checksum' ($r.Code -eq 1 -and $r.Out -match 'does not match' -and (Get-FileHash $Exe -Algorithm SHA256).Hash -eq $before -and @(Get-ChildItem "$u\dev").Count -eq 1) "$($r.Code) $($r.Out)"
            "$($released.ToLower())  agentprobe.exe" | Set-Content "$u\feed\agentprobe.exe.sha256" -Encoding ascii

            $share2 = Start-Process "$u\target\agentprobe.exe" -ArgumentList 'share', '--trust', $key2, '--name', 'updtgt' -WindowStyle Minimized -PassThru
            $up = $false; 1..30 | ForEach-Object { if (-not $up) { Start-Sleep -Milliseconds 700; if ((AP list).Out -match '(?m)^updtgt\s') { $up = $true } } }
            if (-not $up) { Check 'update test: the target copy connects' $false (AP list).Out }
            else {
                [void](AP -t updtgt exec "'kept' | Set-Content kept.txt")
                $psi = New-Object System.Diagnostics.ProcessStartInfo $Exe, '-t updtgt exec --timeout 90s "Start-Sleep 25; Write-Output slept"'
                $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
                $waiting = [System.Diagnostics.Process]::Start($psi); $waitErr = $waiting.StandardError.ReadToEndAsync(); $waitOut = $waiting.StandardOutput.ReadToEndAsync()
                Start-Sleep 3
                $r = AP update
                Check 'update while a command runs: explained and held back (exit 4), nothing changed' ($r.Code -eq 4 -and $r.Out -match 'Commands are running right now: updtgt' -and $r.Out -match 'update --force' -and (Get-FileHash $Exe -Algorithm SHA256).Hash -eq $before) "$($r.Code) $($r.Out)"

                $daemonBefore = @(Get-CimInstance Win32_Process -Filter "Name='agentprobe.exe'" | Where-Object { $_.ExecutablePath -eq $Exe -and $_.CommandLine -match '\sdaemon' })[0].ProcessId
                $r = AP update --force
                Check 'update --force installs the release: the exe is now the released file' ($r.Code -eq 0 -and (Get-FileHash $Exe -Algorithm SHA256).Hash -eq $released) "$($r.Code) $($r.Out)"
                [void]$waiting.WaitForExit(30000)
                Check 'a command that was waiting is told the run number to fetch its result with' ($waitErr.Result -match 'connection to updtgt lost\. The command keeps running there as run #(\d+)') $waitErr.Result
                $back = $false; 1..40 | ForEach-Object { if (-not $back) { Start-Sleep -Milliseconds 700; $back = (AP list).Out -match '(?m)^updtgt\s' } }
                $daemonAfter = @(Get-CimInstance Win32_Process -Filter "Name='agentprobe.exe'" | Where-Object { $_.ExecutablePath -eq $Exe -and $_.CommandLine -match '\sdaemon' })[0].ProcessId
                Check 'the background process restarted from the new exe and the PC reconnected by itself' ($back -and $daemonAfter -and $daemonAfter -ne $daemonBefore) "before $daemonBefore after $daemonAfter`n$((AP list).Out)"
                $r = AP -t updtgt update --check
                Check 'target update --check only compares versions' ($r.Code -eq 0 -and $r.Out -match 'updtgt: agentprobe \S+, the same as this PC' -and (Get-FileHash "$u\target\agentprobe.exe" -Algorithm SHA256).Hash -eq $before) $r.Out
                $h = ''; 1..60 | ForEach-Object { if ($h -notmatch 'orphaned exit 0') { Start-Sleep 1; $h = (AP -t updtgt history --last 5).Out } }
                Check 'the command kept running on the target and its result is still there' ($h -match 'orphaned exit 0' -and (AP -t updtgt result 2).Out -match 'slept') $h
                Check 'nothing is left next to the exe after the update' (@(Get-ChildItem "$u\dev").Count -eq 1) ((Get-ChildItem "$u\dev").Name -join ', ')

                # The connected PC is brought to this PC's exe; its session, approval and workspace carry over.
                $r = AP -t updtgt update
                Check 'target update: the same version is not sent again without --force' ($r.Code -eq 0 -and $r.Out -match 'already runs' -and (Get-FileHash "$u\target\agentprobe.exe" -Algorithm SHA256).Hash -eq $before) $r.Out
                $r = AP -t updtgt update --force
                Check 'target update: the PC restarts from this PC''s exe and reconnects' ($r.Code -eq 0 -and $r.Out -match 'updtgt is back on agentprobe' -and (Get-FileHash "$u\target\agentprobe.exe" -Algorithm SHA256).Hash -eq $released) "$($r.Code) $($r.Out)"
                $r = AP -t updtgt exec 'Get-Content kept.txt; Write-Output after'
                Check 'target update: no new approval, and the workspace is still there' ($r.Code -eq 0 -and $r.Out -match 'kept' -and $r.Out -match 'after') "$($r.Code) $($r.Out)"
                Start-Sleep 2
                Check 'target update: nothing is left next to the target''s exe' (@(Get-ChildItem "$u\target" -Filter 'agentprobe.exe*').Count -eq 1) ((Get-ChildItem "$u\target").Name -join ', ')
                [void](AP -t updtgt end); Start-Sleep 2
            }
        }
        finally {
            [void](AP stop)
            Stop-Process -Id $feed.Id -Force -ErrorAction SilentlyContinue
            Get-CimInstance Win32_Process -Filter "Name='agentprobe.exe'" | Where-Object { $_.ExecutablePath -like "$u\*" } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
            $Exe = $mainExe; $env:AGENTPROBE_INSTANCE = $mainInstance; $env:AGENTPROBE_PORT = $mainPort; Remove-Item Env:AGENTPROBE_UPDATE_URL -ErrorAction SilentlyContinue
        }
    }
}
finally {
    foreach ($p in @($helper, $leak, $form, $leak2, $watchParent)) { if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } }
    Pop-Location
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "$script:pass passed, $script:fail failed" -ForegroundColor $(if ($script:fail) { 'Red' } else { 'Green' })
if ($script:fail) { $script:failed | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red } }
exit $script:fail
