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
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false   # agentprobe writes UTF-8; decode it as such on any code page
$Exe = (Resolve-Path $Exe).Path
$work = Join-Path $env:TEMP ("agentprobe-verify-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $work | Out-Null
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
    Check 'net shows our listening port with our pid' ($r.Out -match "(?m)^\s*$port\s+127\.0\.0\.1\s+powershell\.exe\($PID\)") $r.Out
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

    $korean = Join-Path $work 'cp949.txt'
    [IO.File]::WriteAllBytes($korean, [Text.Encoding]::GetEncoding(949).GetBytes("한글 줄`r`nsecond"))
    Check 'read decodes an ANSI (cp949) file' ((AP -t local read $korean).Out -match '한글 줄') ''
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
Add-Type -AssemblyName System.Windows.Forms
[Windows.Forms.Application]::EnableVisualStyles()
`$f = New-Object Windows.Forms.Form; `$f.Text = 'AP Verify Window'; `$f.Width = 440; `$f.Height = 190; `$f.StartPosition = 'Manual'; `$f.Left = 160; `$f.Top = 160; `$f.TopMost = `$true
`$t = New-Object Windows.Forms.TextBox; `$t.Name = 'inputBox'; `$t.Left = 20; `$t.Top = 20; `$t.Width = 300
`$b = New-Object Windows.Forms.Button; `$b.Name = 'pressButton'; `$b.Text = 'Press me'; `$b.Left = 20; `$b.Top = 60; `$b.Width = 120
`$b.Add_Click({ [IO.File]::WriteAllText('$clickFile', 'clicked:' + `$t.Text) })
`$f.Controls.Add(`$t); `$f.Controls.Add(`$b); [Windows.Forms.Application]::Run(`$f)
"@
    $form = Start-Process powershell -ArgumentList '-NoProfile', '-STA', '-WindowStyle', 'Hidden', '-EncodedCommand', ([Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($formScript))) -PassThru
    $w = AP -t local wait --window 'AP Verify Window' --timeout 20s
    Check 'wait --window sees the test window' ($w.Code -eq 0) $w.Out
    $win = AP -t local windows 'AP Verify'
    Check 'windows lists it with its pid' ($win.Out -match "\s$($form.Id)\s+powershell\.exe") $win.Out

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
            $share = Start-Process $Exe -ArgumentList 'share', '--yes', '--name', 'verify' -WindowStyle Minimized -PassThru
            $up = $false; 1..30 | ForEach-Object { if (-not $up) { Start-Sleep -Milliseconds 700; if ((AP list).Out -match '(?m)^verify\s') { $up = $true } } }
            Check 'a local target connects by discovery' $up (AP list).Out
            if ($up) {
                # The start screen as text (piped): the link, the MCP command with this exe's path, the connected PC.
                $homeText = (AP home).Out
                Check 'home: prints the dashboard link, the MCP command for this exe and the connected PC' ($homeText -match 'http://127\.0\.0\.1:\d+/\?k=[0-9A-F]{16,}' -and $homeText.Contains("claude mcp add agentprobe -- `"$Exe`" mcp") -and $homeText -match '(?m):\s.*\bverify\b.*$') $homeText
                $bare = AP
                Check 'no arguments, piped: prints the command list instead of opening a screen' ($bare.Code -eq 0 -and $bare.Out -match 'ON THE PC TO TEST') (($bare.Out -split "`n" | Select-Object -First 2) -join ' / ')

                # The leak verdict needs two minutes of evidence: start watching now and judge at the end of this section.
                $leak2 = Start-Process powershell -ArgumentList '-NoProfile', '-Command', $leakScript -WindowStyle Hidden -PassThru
                Start-Sleep 2
                [void](AP -t verify watch start $leak2.Id $helper.Id --interval 1s)
                $watchStarted = Get-Date

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
                [void](AP -t verify push $tree 'treecopy'); $treeBack = Join-Path $work 'treeback'; [void](AP -t verify pull 'treecopy' $treeBack)
                Check 'folder push + pull keeps the structure' ((Test-Path "$treeBack\sub\deep\k.txt") -and (Get-Content "$treeBack\sub\deep\k.txt" -Encoding UTF8) -eq '한글') ''

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
                $e = AP -t verify end
                Check 'end closes the session and removes the workspace' ($e.Code -eq 0 -and $share.WaitForExit(8000)) $e.Out
            }
            if (-not $share.HasExited) { Stop-Process -Id $share.Id -Force }
            [void](AP stop)
        }
    }
}
finally {
    foreach ($p in @($helper, $leak, $form, $leak2)) { if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } }
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "$script:pass passed, $script:fail failed" -ForegroundColor $(if ($script:fail) { 'Red' } else { 'Green' })
if ($script:fail) { $script:failed | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red } }
exit $script:fail
