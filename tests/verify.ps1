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

    # A thread that was given a name, in a process whose start time is known.
    $namedScript = Join-Path $work 'named.ps1'
    @'
Add-Type -Namespace ApVerify -Name Thr -MemberDefinition '[DllImport("kernel32.dll")] public static extern IntPtr GetCurrentThread(); [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern int SetThreadDescription(IntPtr thread, string description);'
[void][ApVerify.Thr]::SetThreadDescription([ApVerify.Thr]::GetCurrentThread(), 'ApVerify Named Thread')
Start-Sleep 600
'@ | Set-Content $namedScript -Encoding UTF8
    $named = Start-Process powershell -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$namedScript`"" -WindowStyle Hidden -PassThru
    Start-Sleep 4
    $r = AP -t local threads $named.Id --grep 'ApVerify Named' --interval 300ms
    $row = ($r.Out -split "`n" | Where-Object { $_ -match 'ApVerify Named Thread' } | Select-Object -First 1)
    Check 'threads --grep finds the thread by its name, and only that one' ($r.Code -eq 0 -and $r.Out -match '(?m)^1 of \d+ threads match' -and $row) $r.Out
    if ("$row" -match '^\s*(\d+)\s.*\s(\d\d)-(\d\d) (\d\d):(\d\d):(\d\d)\s') {
        $shown = Get-Date -Month $Matches[2] -Day $Matches[3] -Hour $Matches[4] -Minute $Matches[5] -Second $Matches[6]
        $thread = (Get-Process -Id $named.Id).Threads | Where-Object Id -eq ([int]$Matches[1])
        Check 'threads: CREATED is when Windows says that thread started' ($thread -and [Math]::Abs(($shown - $thread.StartTime).TotalSeconds) -le 2) "row: $row / Windows: thread $($thread.Id) $($thread.StartTime)"
    } else { Check 'threads: CREATED column' $false $r.Out }
    $r = AP -t local threads $named.Id --by-name --interval 300ms
    Check 'threads --by-name counts the threads per name' ($r.Out -match '(?m)^\s*1\s+\S+\s+\d\d-\d\d \d\d:\d\d:\d\d\s+ApVerify Named Thread\s*$' -and $r.Out -match '\(no name\)') $r.Out
    $r = AP -t local threads $named.Id --by-name --grep 'no name' --interval 300ms
    Check 'threads --by-name --grep does not take the placeholder "(no name)" for a name' ($r.Out -notmatch '\(no name\)' -and $r.Out -match 'no thread name matches') $r.Out
    $r = AP -t local threads $named.Id --grep 'NoSuchThreadNameZz' --interval 300ms
    Check 'threads --grep with no match says how many threads were looked at' ($r.Out -match '(?m)^0 of \d+ threads match') $r.Out
    Stop-Process -Id $named.Id -Force

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

    # ------------------------------------------------------------------ the PC as a whole
    Write-Host "`nthe PC as a whole (event logs, starts and ends, drivers, devices, sensors)"
    $since30 = (Get-Date).AddDays(-30)
    # Not StartTime in the hashtable: Windows PowerShell 5.1 fails to read it back on some cultures and then drops it.
    $sysEvents = { param($provider, $id) @(Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = $provider; Id = $id } -ErrorAction SilentlyContinue | Where-Object { $_.TimeCreated -ge $since30 }) }
    $kg12 = & $sysEvents 'Microsoft-Windows-Kernel-General' 12
    if ($kg12.Count -gt 0) {
        # Its message has the start time with direction marks (U+200E) around the digits: wevtutil turned them into '?'.
        $r = AP -t local events --provider Microsoft-Windows-Kernel-General --id 12 --level info --since '30d' --max 1
        Check 'events: direction marks in a message come out as nothing, not as ?' ($r.Code -eq 0 -and $r.Out -match 'Kernel-General#12: .*\d{4}-\d\d-\d\dT\d\d:\d\d' -and $r.Out -notmatch "[\u200E\u200F?]\d") $r.Out
        $startTime = (([xml]$kg12[0].ToXml()).Event.EventData.Data | Where-Object Name -eq 'StartTime').'#text'
        $r = AP -t local events --provider Microsoft-Windows-Kernel-General --id 12 --level info --since '30d' --max 1 --data
        Check 'events --data: the event''s own fields, as Windows recorded them' ($r.Out -match "(?m)^  data: .*StartTime=$([regex]::Escape($startTime))") "$startTime / $($r.Out)"
        $r = AP -t local events --provider 'NoSuchProviderForVerifyZz,Microsoft-Windows-Kernel-General' --id 12 --level info --since '30d' --max 1
        Check 'events --provider takes several names' ($r.Out -match 'Kernel-General#12') $r.Out
    }
    # A provider can be named by its classic source name (BugCheck, DCOM): one that differs from its name, if the log has one.
    $classic = Get-WinEvent -LogName System -MaxEvents 3000 -ErrorAction SilentlyContinue | Where-Object { $_.TimeCreated -ge $since30 } | ForEach-Object {
        $p = ([xml]$_.ToXml()).Event.System.Provider
        if ($p.EventSourceName -and $p.EventSourceName -ne $p.Name) { [pscustomobject]@{ Source = $p.EventSourceName; Name = $p.Name; Id = $_.Id } }
    } | Select-Object -First 1
    if ($classic) {
        $r = AP -t local events --provider $classic.Source --id $classic.Id --level info --since '30d' --max 1
        Check 'events --provider: a classic source name finds the events of its provider' ($r.Out -match "$([regex]::Escape($classic.Name))#$($classic.Id)") "$($classic.Source) -> $($classic.Name): $($r.Out)"
    }
    $r = AP -t local events --provider NoSuchProviderForVerifyZz --level info --since '1d'
    Check 'events: a provider that does not exist is named as such, not only "no events"' ($r.Out -match 'provider NoSuchProviderForVerifyZz: NOT registered on this PC') $r.Out
    $r = AP -t local events --provider Microsoft-Windows-Kernel-General --id 59999 --level info --since '1d'
    Check 'events: a provider that exists but logged nothing is said to exist' ($r.Out -match 'provider Microsoft-Windows-Kernel-General: registered on this PC') $r.Out
    $newest = Get-WinEvent -LogName System -MaxEvents 1
    $at = $newest.TimeCreated.ToString('yyyy-MM-dd HH:mm:ss', [Globalization.CultureInfo]::InvariantCulture)
    $r = AP -t local events --around "$at,2000-01-01 03:00" --log System
    Check 'events --around: the window around an event counts it, a window without one says so' ($r.Code -eq 0 -and $r.Out -match "(?m)^\s+\d+\s+Sys $([regex]::Escape($newest.ProviderName))#$($newest.Id) \(" -and $r.Out -match '(?m)^1 of 2 windows had no event at all: 01-01 03:00:00') $r.Out
    $r = AP -t local events --around $at --all-logs --provider $newest.ProviderName --id $newest.Id
    Check 'events --around --all-logs: searches every log that holds events' ($r.Code -eq 0 -and $r.Out -match 'searched \d+ logs that hold events' -and $r.Out -match "(?m)^\s+1\s+Sys $([regex]::Escape($newest.ProviderName))#$($newest.Id) \(") $r.Out

    # boots against the events Windows logged: one start per Kernel-General 12 (plus a fast startup or a resume
    # from hibernation, a Kernel-Boot 27 without one), one unexpected end per Kernel-Power 41 or EventLog 6008.
    $kb27 = & $sysEvents 'Microsoft-Windows-Kernel-Boot' 27
    $kp41 = & $sysEvents 'Microsoft-Windows-Kernel-Power' 41
    $el6008 = & $sysEvents 'EventLog' 6008
    $unexpected = $null
    $r = AP -t local boots --since '30d' --max 500
    if ($r.Out -match '(?m)^(\d+) starts in the last 30d00h; ends: (\d+) UNEXPECTED') {
        $starts = [int]$Matches[1]; $unexpected = [int]$Matches[2]
        $others = @($kb27 | Where-Object { $t27 = $_.TimeCreated; -not ($kg12 | Where-Object { [Math]::Abs(($_.TimeCreated - $t27).TotalMinutes) -le 5 }) }).Count
        Check 'boots: as many starts as Windows logged' ($starts -eq $kg12.Count + $others) "boots $starts, Windows $($kg12.Count) + $others"
        Check 'boots: as many unexpected ends as Kernel-Power 41 / EventLog 6008 say' ($unexpected -ge [Math]::Max($kp41.Count, $el6008.Count) -and $unexpected -le $kp41.Count + $el6008.Count) "boots $unexpected, Windows 41 x$($kp41.Count), 6008 x$($el6008.Count)"
        $latest = $kg12 | Sort-Object TimeCreated | Select-Object -Last 1
        if ($latest -and -not ($kb27 | Where-Object { $_.TimeCreated -gt $latest.TimeCreated.AddMinutes(5) })) {
            $began = ([datetime](([xml]$latest.ToXml()).Event.EventData.Data | Where-Object Name -eq 'StartTime').'#text').ToString('MM-dd HH:mm:ss', [Globalization.CultureInfo]::InvariantCulture)
            Check 'boots: the start that runs now began when the kernel says' ($r.Out -match "(?m)^$began\s+up \S+\s+running now") "$began / $(($r.Out -split "`n") -match 'running now')"
        }
    } else { Check 'boots summary line' $false $r.Out }
    # --logs: the file of a folder last written during the current start, its lines and its last line. UTF-16 with
    # U+AC0A, whose low byte is the byte of a line break: three lines, not four.
    $logDir = Join-Path $work 'applogs'; New-Item -ItemType Directory -Path $logDir | Out-Null
    $odd = [char]0xAC0A
    [IO.File]::WriteAllText((Join-Path $logDir 'app-1.log'), "start $odd`r`nworking $odd`r`nlast line $odd", [Text.Encoding]::Unicode)
    $r = AP -t local boots --since '365d' --max 1 --logs $logDir
    Check 'boots --logs: the log file last written during this start, its line count and its last line' ($r.Out -match "(?m)^\s+log: app-1\.log last written \d\d:\d\d:\d\d, 3 lines, last line `"last line $odd`" \(1 file created during it\)") $r.Out

    $r = AP -t local crashes --system --since '30d'
    $cc = Get-ItemProperty HKLM:\SYSTEM\CurrentControlSet\Control\CrashControl
    $kind = switch ($cc.CrashDumpEnabled) { 0 { 'NONE' } 1 { if ($cc.FilterPages -eq 1) { 'active memory dump' } else { 'complete memory dump' } } 2 { 'kernel memory dump' } 3 { 'small memory dumps' } 7 { 'automatic memory dump' } default { "CrashDumpEnabled=$($cc.CrashDumpEnabled)" } }
    Check 'crashes --system: the dump setup is what CrashControl says' ($r.Code -eq 0 -and $r.Out -match "(?m)^dump setup: $([regex]::Escape($kind))" -and $r.Out -match $(if ($cc.AutoReboot -eq 0) { 'stays on the blue screen' } else { 'restarts by itself after a blue screen' })) $r.Out
    $pageOnBoot = @((Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management').ExistingPageFiles) -match "^\\\?\?\\$($env:SystemDrive)"
    Check 'crashes --system: says whether the boot volume has a page file, as Windows does' (($r.Out -match "page file $([regex]::Escape($env:SystemDrive))\\") -eq [bool]$pageOnBoot) "$pageOnBoot / $(($r.Out -split "`n") -match '^dump setup')"
    if ($null -ne $unexpected) {
        Check 'crashes --system: as many unexpected shutdowns as boots, and a verdict' ($r.Out -match '(?m)^unexpected shutdowns: (none|(\d+))' -and [int]("0" + $Matches[2]) -eq $unexpected -and $r.Out -match '(?m)^-> ') $r.Out
        $tr = (AP -t local triage).Out
        Check 'triage: a line about unexpected shutdowns exactly when there were some in 30 days' (($unexpected -gt 0) -eq ($tr -match '(?m)^resets\s+\d+ unexpected shutdown')) $tr
        if ($tr -match 'pending reboot can explain') { Check 'triage: the pending reboot is the last lead' ((@($tr -split "`n" | Where-Object { $_ -match '^\s+- ' }) | Select-Object -Last 1) -match 'pending reboot') $tr }
    }

    $wakeNames = @{ 1 = 'other'; 2 = 'unknown'; 3 = 'APM timer'; 4 = 'modem ring'; 5 = 'LAN remote'; 6 = 'power switch'; 7 = 'PCI PME#'; 8 = 'AC power restored' }
    $wake = [int](Get-CimInstance Win32_ComputerSystem).WakeUpType
    if ($wakeNames.ContainsKey($wake)) {
        $r = AP -t local sys
        Check 'sys: what started the PC is the SMBIOS wake-up type Windows reports' ($r.Out -match "(?m)^boot\s.*started by: $([regex]::Escape($wakeNames[$wake]))") "Windows: $wake / $(($r.Out -split "`n") -match '^boot')"
    }

    $r = AP -t local drivers --all
    $runningDrivers = @(Get-CimInstance Win32_SystemDriver -Filter "State='Running'").Count
    if ($r.Out -match '^(\d+) kernel drivers running as services') { Check 'drivers: as many running drivers as Windows lists' (Near ([double]$Matches[1]) $runningDrivers 0 3) "got $($Matches[1]), Windows $runningDrivers" } else { Check 'drivers summary line' $false $r.Out }
    $tcpip = Get-Item "$env:WINDIR\System32\drivers\tcpip.sys"
    $tv = $tcpip.VersionInfo
    $r = AP -t local drivers --grep '^tcpip$'
    Check 'drivers: a driver''s version, date and path are its file''s' ($r.Out -match "(?m)^tcpip\s+$($tv.FileMajorPart)\.$($tv.FileMinorPart)\.$($tv.FileBuildPart)\.$($tv.FilePrivatePart)\s+$($tcpip.LastWriteTime.ToString('yyyy-MM-dd'))\s.*Microsoft.*$([regex]::Escape($tcpip.FullName))") $r.Out
    $r = AP -t local drivers
    Check 'drivers: without --all, the non-Microsoft ones and how many were left out' ($r.Out -match '^(\d+) kernel drivers running as services; (\d+) not from Microsoft .*?, (\d+) from Microsoft left out' -and [int]$Matches[2] + [int]$Matches[3] -eq [int]$Matches[1]) ($r.Out -split "`n" | Select-Object -First 1)

    $pnp = @(Get-PnpDevice -PresentOnly)
    $r = AP -t local devices
    if ($r.Out -match '^(\d+) devices present, (\d+) with a problem') {
        Check 'devices: as many present devices as Get-PnpDevice' (Near ([double]$Matches[1]) $pnp.Count 0 2) "got $($Matches[1]), Windows $($pnp.Count)"
        $problems = @($pnp | Where-Object { $_.ConfigManagerErrorCode -ne 0 }).Count
        Check 'devices: as many with a problem as Windows' ([int]$Matches[2] -eq $problems) "got $($Matches[2]), Windows $problems"
    } else { Check 'devices summary line' $false $r.Out }
    $link = $pnp | Where-Object InstanceId -like 'PCI\*' | ForEach-Object {
        $p = @(Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName DEVPKEY_PciDevice_CurrentLinkSpeed, DEVPKEY_PciDevice_CurrentLinkWidth -ErrorAction SilentlyContinue)
        if ($p.Count -eq 2 -and $p[0].Data -and $p[1].Data) { [pscustomobject]@{ Id = $_.InstanceId; Speed = $p[0].Data; Width = $p[1].Data } }
    } | Select-Object -First 1
    if ($link) {
        $r = AP -t local devices --grep "^$([regex]::Escape($link.Id))$" --long
        Check 'devices --long: the PCIe link is the one Windows reports' ($r.Out -match "(?m)^\s+link\s+PCIe Gen$($link.Speed) x$($link.Width),") "$($link.Id) Gen$($link.Speed) x$($link.Width): $($r.Out)"
    }

    $r = AP -t local sensors
    Check 'sensors: answers, and says what it cannot read' ($r.Code -eq 0 -and $r.Out -match '(?m)^not readable here: voltages') $r.Out
    foreach ($z in @(Get-CimInstance Win32_PerfFormattedData_Counters_ThermalZoneInformation -ErrorAction SilentlyContinue) | Select-Object -First 2) {
        $celsius = $z.Temperature - 273.15
        if ($r.Out -match "(?m)^thermal\s+ACPI $([regex]::Escape($z.Name)): (-?[\d.]+)") { Check "sensors: thermal zone $($z.Name) as its counter says" (Near ([double]$Matches[1]) $celsius 0 3) "got $($Matches[1]), counter $celsius" }
        else { Check "sensors: thermal zone $($z.Name)" $false $r.Out }
    }
    if (([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        foreach ($d in @(Get-PhysicalDisk)) {
            $t = ($d | Get-StorageReliabilityCounter).Temperature
            if ($t -gt 0 -and $r.Out -match "(?m)^disk\s+PhysicalDrive$($d.DeviceId) .*?: (\d+)") { Check "sensors: disk $($d.DeviceId) temperature as Windows reports it" (Near ([double]$Matches[1]) $t 0 3) "got $($Matches[1]), Windows $t" }
        }
    }

    # ------------------------------------------------------------------ files
    Write-Host "`nfiles"
    $file = Join-Path $work 'lines.txt'
    1..500 | ForEach-Object { "line $_ value=$($_ * 7)" } | Set-Content $file -Encoding UTF8
    Check 'read --tail 3' ((AP -t local read $file --tail 3).Out.Trim() -eq "line 498 value=3486`nline 499 value=3493`nline 500 value=3500") ''
    Check 'read --head 2' ((AP -t local read $file --head 2).Out.Trim() -eq "line 1 value=7`nline 2 value=14") ''
    Check 'read --lines 100-101 numbers the lines' ((AP -t local read $file --lines 100-101).Out.Trim() -eq "100: line 100 value=700`n101: line 101 value=707") ''
    $g = (AP -t local read $file --grep 'value=35$').Out.Trim()
    Check 'read --grep returns exactly the match' ($g -eq '5: line 5 value=35') $g
    Check '-t local refuses commands that need a session' ((AP -t local read $file --new).Code -eq 2 -and (AP -t local watch start explorer).Code -eq 2 -and (AP -t local deps $Exe --run).Code -eq 2) ''

    # An option of agentprobe after the command would be sent to the target as part of the command: refused, with the way to write it.
    $r = AP -t local exec 'Write-Output must-not-run' --full
    Check 'exec: an agentprobe option after the command is refused, not sent along as part of the command' ($r.Code -eq 2 -and $r.Out -match "'--full' comes after the command" -and $r.Out -notmatch '(?m)^must-not-run') "$($r.Code) $($r.Out)"
    Check 'exec: the same text inside the quotes is the command''s own business' ((AP -t local exec 'Write-Output ok --full').Out -match '(?m)^ok$') ''

    # The firewall rules Windows keeps for programs of agentprobe workspaces, against what Windows has stored.
    $stored = @((Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules').PSObject.Properties | Where-Object { $_.Value -is [string] -and $_.Value -match '\|App=[^|]*\\agentprobe\\work-' })
    $r = AP -t local firewall leftovers
    if ($stored.Count -eq 0) { Check 'firewall leftovers: none stored, none listed' ($r.Code -eq 0 -and $r.Out -match '^no rule for a program in an agentprobe workspace') $r.Out }
    else { Check 'firewall leftovers lists as many rules as Windows has stored for workspace programs' ($r.Code -eq 0 -and $r.Out -match "(?m)^(\.\.\. \d+ more\. )?$($stored.Count) rules? for programs in agentprobe workspaces") "stored: $($stored.Count)`n$($r.Out)" }

    # The lines of a time span: from the first time up to, not including, the second; a line without a time
    # (a stack trace) belongs to the line before it.
    $timed = Join-Path $work 'timed.log'
    @('2026-01-01 11:59:59 before', '2026-01-01 12:02:00 first', '   at Some.Frame()', '[12:03:30.250] middle', '2026-01-01 12:04:59 last', '2026-01-01 12:05:00 after', 'no time, after', '1/1/2026 1:03:00 PM afternoon') | Set-Content $timed -Encoding UTF8
    $r = AP -t local read $timed --between 12:02 12:05
    Check 'read --between: the lines from the first time up to the second, with the lines that belong to them' ($r.Out.Trim() -eq "2: 2026-01-01 12:02:00 first`n3:    at Some.Frame()`n4: [12:03:30.250] middle`n5: 2026-01-01 12:04:59 last") $r.Out
    $r = AP -t local read --between 12:02-12:05 $timed --grep 'first|after'
    Check 'read --between combines with --grep' ($r.Out.Trim() -eq '2: 2026-01-01 12:02:00 first') $r.Out
    $r = AP -t local read $timed --between 23:00 00:30
    Check 'read --between with nothing in the span: says what span the file does cover' ($r.Out -match 'no line from 23:00:00 up to 00:30:00' -and $r.Out -match 'run from 11:59:59 to 13:03:00') $r.Out
    $r = AP -t local read $timed --between 13:00 13:10
    Check 'read --between reads a 12-hour clock (1:03:00 PM is 13:03)' ($r.Out.Trim() -eq '8: 1/1/2026 1:03:00 PM afternoon') $r.Out
    $r = AP -t local read $timed --between 12:02 12:05 --lines 3-4
    Check 'read --between --lines: a line range that starts on a line without a time still knows whose line it is' ($r.Out.Trim() -eq "3:    at Some.Frame()`n4: [12:03:30.250] middle") $r.Out
    $r = AP -t local read $file --between 12:00 13:00
    Check 'read --between on a file without times says so, not "no lines"' ($r.Out -match 'no line of this file has a time of day') $r.Out

    # What a shortcut starts, compared with what was written into it.
    $lnk = Join-Path $work 'ApVerify Shortcut.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $sc = $shell.CreateShortcut($lnk); $sc.TargetPath = "$env:WINDIR\System32\winver.exe"; $sc.Arguments = '--alpha "two words"'; $sc.WorkingDirectory = $env:WINDIR; $sc.Save()
    $r = AP -t local stat $lnk
    Check 'stat on a shortcut: its target, arguments and start folder' ($r.Out -match "(?m)^\s+shortcut\s+target\s+$([regex]::Escape("$env:WINDIR\System32\winver.exe"))\s*$" -and $r.Out -match '(?m)^\s+arguments --alpha "two words"\s*$' -and $r.Out -match "(?m)^\s+start in\s+$([regex]::Escape($env:WINDIR))\s*$") $r.Out

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

    # stat --strings: a .NET string literal (with Korean) at the offset given, and UTF-8 / UTF-16 text in a binary.
    $lit = Join-Path $work 'ApVerifyLiterals.dll'
    Add-Type -TypeDefinition 'public static class ApVerifyLiterals { public static string Text() { return "ApVerify literal \uD55C\uAE00 text"; } }' -OutputAssembly $lit -OutputType Library
    $hangul = "$([char]0xD55C)$([char]0xAE00)"
    $r = AP -t local stat $lit --strings 'ApVerify literal'
    if ($r.Out -match '(?m)^\s+0x([0-9A-F]{8})\s+#US\s+"ApVerify literal (.+) text"') {
        $shownText = $Matches[2]
        $atOffset = [Text.Encoding]::Unicode.GetString([IO.File]::ReadAllBytes($lit), [Convert]::ToInt32($Matches[1], 16), 48)
        Check 'stat --strings: a .NET string literal, its Korean intact, at the offset given' ($shownText -eq $hangul -and $atOffset -eq "ApVerify literal $hangul text") "$atOffset / $($r.Out)"
    } else { Check 'stat --strings finds a .NET string literal' $false $r.Out }
    $blob = Join-Path $work 'blob.bin'
    $utf8 = [Text.Encoding]::UTF8.GetBytes("ApVerify utf8 $hangul line")
    [IO.File]::WriteAllBytes($blob, [byte[]](@(0, 1, 2, 3) + $utf8 + @(0, 0, 0) + [Text.Encoding]::Unicode.GetBytes('ApVerify wide line') + @(0, 0, 9)))
    $r = AP -t local stat $blob --strings 'ApVerify'
    Check 'stat --strings: UTF-8 and UTF-16 text in a binary, each at its offset' ($r.Out -match '2 different texts match' -and $r.Out -match "(?m)^\s+0x00000004\s+utf8\s+`"ApVerify utf8 $hangul line`"" -and $r.Out -match "(?m)^\s+0x$('{0:X8}' -f (4 + $utf8.Length + 3))\s+utf16\s+`"ApVerify wide line`"") $r.Out

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

    # A folder that looks like a self-contained .NET app (its own host and runtime, a runtimeconfig without a
    # framework) but has no App.deps.json: the .NET host does not start that from its own folder.
    $sc8 = Join-Path $work 'selfcontained'; New-Item -ItemType Directory $sc8 | Out-Null
    Copy-Item "$env:WINDIR\System32\where.exe" (Join-Path $sc8 'App.exe')
    foreach ($n in 'hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll', 'there.dll') { Copy-Item "$env:WINDIR\System32\version.dll" (Join-Path $sc8 $n) }
    '{"runtimeOptions":{"configProperties":{}}}' | Set-Content (Join-Path $sc8 'App.runtimeconfig.json')
    $r = AP -t local deps (Join-Path $sc8 'App.exe')
    Check 'deps: a self-contained .NET app without its deps.json is reported (the host trap)' ($r.Code -eq 1 -and $r.Out -match '(?m)^HOST TRAP\s+App\.deps\.json is missing' -and $r.Out -match '0x80008081') $r.Out
    $fxr11 = Get-ChildItem "$env:ProgramFiles\dotnet\host\fxr" -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^(\d+)\.' -and [int]$Matches[1] -ge 11 } | Select-Object -First 1
    if ($fxr11) {
        $sc11 = Join-Path $work 'selfcontained11'; Copy-Item $sc8 $sc11 -Recurse; Copy-Item (Join-Path $fxr11.FullName 'hostfxr.dll') (Join-Path $sc11 'hostfxr.dll') -Force
        $r = AP -t local deps (Join-Path $sc11 'App.exe')
        Check 'deps: with a .NET 11 host in the folder the missing deps.json is noted, not called a trap (that host starts the app)' ($r.Out -notmatch 'HOST TRAP' -and $r.Out -match 'no App\.deps\.json \(the \.NET \d+ host starts the app without it') $r.Out
    }
    '{"runtimeTarget":{"name":".NETCoreApp,Version=v8.0/win-x64"},"targets":{".NETCoreApp,Version=v8.0/win-x64":{"App/1.0.0":{"runtime":{"there.dll":{}}},"Lib/2.0.0":{"runtime":{"lib/net8.0/NotThere.dll":{}},"native":{"coreclr.dll":{}}}}}}' | Set-Content (Join-Path $sc8 'App.deps.json')
    $r = AP -t local deps (Join-Path $sc8 'App.exe')
    Check 'deps: files the deps.json lists are checked against the folder; no host trap once it is there' ($r.Code -eq 1 -and $r.Out -notmatch 'HOST TRAP' -and $r.Out -match '(?m)^MISSING\s+1 of the 3 files that App\.deps\.json lists is not in the app''s folder: NotThere\.dll \(Lib/2\.0\.0\)') $r.Out

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
    $touchFile = Join-Path $work 'touched.txt'
    $tapsFile = Join-Path $work 'taps.txt'
    $toggleFile = Join-Path $work 'toggled.txt'
    $hwndFile = Join-Path $work 'hwnd.txt'
    $affinityFile = Join-Path $work 'affinity.txt'
    $formScript = @"
Add-Type -AssemblyName System.Windows.Forms, PresentationFramework, PresentationCore, WindowsBase
Add-Type -ReferencedAssemblies System.Windows.Forms, System.Drawing 'namespace ApVerify.Views { public class EditorForm : System.Windows.Forms.Form { public int Number = 42; public string Note = "ap-note"; public bool Flag = true; public System.Drawing.Point Where = new System.Drawing.Point(3, 4); public System.DayOfWeek Day = System.DayOfWeek.Wednesday; public object Nothing; public System.Collections.Generic.List<int> Items = new System.Collections.Generic.List<int>(); public int[] Numbers = new int[7]; public string Auto { get; set; } public EditorForm() { Auto = "auto-value"; } } public class Cache<T> { public class Node { } } public static class Holder { public static object[] Nodes = new object[] { new Cache<int>.Node(), new Cache<int>.Node(), new Cache<int>.Node(), new Cache<int>.Node(), new Cache<int>.Node() }; } public static class Input { [System.Runtime.InteropServices.DllImport("user32.dll")] static extern System.IntPtr GetMessageExtraInfo(); public static bool FromTouch() { return (GetMessageExtraInfo().ToInt64() & 0xFFFFFF80) == 0xFF515780; } [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(System.IntPtr hwnd, uint affinity); } }'
`$nodes = [ApVerify.Views.Holder]::Nodes.Length
[Windows.Forms.Application]::EnableVisualStyles()
`$neverShown = 1..3 | ForEach-Object { New-Object ApVerify.Views.EditorForm }
`$wpfNeverShown = 1..2 | ForEach-Object { New-Object System.Windows.Window }
`$wpfShown = New-Object System.Windows.Window; `$wpfShown.Title = 'ApWpf'; `$wpfShown.Width = 220; `$wpfShown.Height = 130; `$wpfShown.Left = 700; `$wpfShown.Top = 520; `$wpfShown.ShowInTaskbar = `$false; `$wpfShown.ShowActivated = `$false
`$wpfSlider = New-Object System.Windows.Controls.Slider; `$wpfSlider.Name = 'wpfSlider'; `$wpfSlider.Minimum = 0; `$wpfSlider.Maximum = 1; `$wpfSlider.Value = 0.25
`$wpfToggle = New-Object System.Windows.Controls.Primitives.ToggleButton; `$wpfToggle.Name = 'wpfToggle'; `$wpfToggle.Content = 'Gear'; `$wpfToggle.Height = 30
`$wpfToggle.Add_Click({ [IO.File]::WriteAllText('$toggleFile', 'toggle-clicked') })
`$wpfPanel = New-Object System.Windows.Controls.StackPanel; [void]`$wpfPanel.Children.Add(`$wpfSlider); [void]`$wpfPanel.Children.Add(`$wpfToggle); `$wpfShown.Content = `$wpfPanel
`$wpfShown.Show()
`$formClosed = New-Object ApVerify.Views.EditorForm; `$formClosed.ShowInTaskbar = `$false; `$formClosed.StartPosition = 'Manual'; `$formClosed.Left = 700; `$formClosed.Top = 620; `$formClosed.Width = 120; `$formClosed.Height = 60; `$formClosed.Show(); `$formClosed.Close()
`$wpfClosed = New-Object System.Windows.Window; `$wpfClosed.Width = 120; `$wpfClosed.Height = 60; `$wpfClosed.Left = 700; `$wpfClosed.Top = 620; `$wpfClosed.ShowInTaskbar = `$false; `$wpfClosed.ShowActivated = `$false; `$wpfClosed.Show(); `$wpfClosed.Close()
`$secret = New-Object Windows.Forms.Form; `$secret.Text = 'AP Verify NoCapture'; `$secret.Width = 200; `$secret.Height = 70; `$secret.StartPosition = 'Manual'; `$secret.Left = 700; `$secret.Top = 420; `$secret.ShowInTaskbar = `$false; `$secret.TopMost = `$true
`$secret.Show()
`$kept = if ([ApVerify.Views.Input]::SetWindowDisplayAffinity(`$secret.Handle, 0x11)) { 'left out' } elseif ([ApVerify.Views.Input]::SetWindowDisplayAffinity(`$secret.Handle, 1)) { 'shown black' } else { 'none' }
[IO.File]::WriteAllText('$affinityFile', `$kept)
`$f = New-Object Windows.Forms.Form; `$f.Text = 'AP Verify Window'; `$f.Width = 440; `$f.Height = 375; `$f.StartPosition = 'Manual'; `$f.Left = 160; `$f.Top = 160; `$f.TopMost = `$true
`$f.Add_Shown({ [IO.File]::WriteAllText('$hwndFile', `$f.Handle.ToInt64().ToString('X')); `$list.TopIndex = 30 })
`$list = New-Object Windows.Forms.ListBox; `$list.Name = 'longList'; `$list.Left = 20; `$list.Top = 240; `$list.Width = 180; `$list.Height = 45; 1..60 | ForEach-Object { [void]`$list.Items.Add('item ' + `$_) }
`$t = New-Object Windows.Forms.TextBox; `$t.Name = 'inputBox'; `$t.Left = 20; `$t.Top = 20; `$t.Width = 300
`$b = New-Object Windows.Forms.Button; `$b.Name = 'pressButton'; `$b.Text = 'Press me'; `$b.Left = 20; `$b.Top = 60; `$b.Width = 120
`$b.Add_Click({ [IO.File]::WriteAllText('$clickFile', 'clicked:' + `$t.Text) })
`$b.Add_MouseDown({ [IO.File]::WriteAllText('$touchFile', `$(if ([ApVerify.Views.Input]::FromTouch()) { 'touch' } else { 'mouse' })); [IO.File]::AppendAllText('$tapsFile', 'x') })
`$check = New-Object Windows.Forms.CheckBox; `$check.Name = 'checkBox'; `$check.Text = 'Check me'; `$check.Left = 300; `$check.Top = 62; `$check.Width = 110; `$check.Checked = `$true
`$tabs = New-Object Windows.Forms.TabControl; `$tabs.Name = 'tabs'; `$tabs.Left = 20; `$tabs.Top = 100; `$tabs.Width = 380; `$tabs.Height = 90
`$tabs.TabPages.Add('First tab'); `$tabs.TabPages.Add('Second tab')
`$twinA = New-Object Windows.Forms.Button; `$twinA.Name = 'twinA'; `$twinA.Text = 'Twin'; `$twinA.Left = 20; `$twinA.Top = 205; `$twinA.Width = 90; `$twinA.Add_Click({ [IO.File]::WriteAllText('$clickFile', 'twin:A') })
`$twinB = New-Object Windows.Forms.Button; `$twinB.Name = 'twinB'; `$twinB.Text = 'Twin'; `$twinB.Left = 120; `$twinB.Top = 205; `$twinB.Width = 90; `$twinB.Add_Click({ [IO.File]::WriteAllText('$clickFile', 'twin:B') })
`$cover = New-Object Windows.Forms.Form; `$cover.Text = 'AP Verify Cover'; `$cover.Width = 200; `$cover.Height = 110; `$cover.StartPosition = 'Manual'; `$cover.Left = 165; `$cover.Top = 372; `$cover.TopMost = `$true; `$cover.ShowInTaskbar = `$false; `$cover.BackColor = [Drawing.Color]::Magenta
`$showCover = New-Object Windows.Forms.Button; `$showCover.Name = 'showCover'; `$showCover.Text = 'Show cover'; `$showCover.Left = 230; `$showCover.Top = 205; `$showCover.Width = 80; `$showCover.Add_Click({ `$cover.Show() })
`$bigCover = New-Object Windows.Forms.Button; `$bigCover.Name = 'bigCover'; `$bigCover.Text = 'Big cover'; `$bigCover.Left = 320; `$bigCover.Top = 250; `$bigCover.Width = 80; `$bigCover.Add_Click({ `$cover.Bounds = `$f.Bounds; `$cover.Show() })
`$listCombo = New-Object Windows.Forms.ComboBox; `$listCombo.Name = 'listCombo'; `$listCombo.DropDownStyle = 'DropDownList'; [void]`$listCombo.Items.Add('Alpha'); [void]`$listCombo.Items.Add('Beta'); `$listCombo.SelectedIndex = 0; `$listCombo.Left = 20; `$listCombo.Top = 295; `$listCombo.Width = 120
`$editCombo = New-Object Windows.Forms.ComboBox; `$editCombo.Name = 'editCombo'; `$editCombo.DropDownStyle = 'DropDown'; [void]`$editCombo.Items.Add('One'); `$editCombo.Left = 160; `$editCombo.Top = 295; `$editCombo.Width = 140
`$hideCover = New-Object Windows.Forms.Button; `$hideCover.Name = 'hideCover'; `$hideCover.Text = 'Hide cover'; `$hideCover.Left = 320; `$hideCover.Top = 205; `$hideCover.Width = 80; `$hideCover.Add_Click({ `$cover.Hide() })
`$d = New-Object Windows.Forms.Form; `$d.Text = 'AP Verify Dialog'; `$d.Width = 260; `$d.Height = 120; `$d.StartPosition = 'Manual'; `$d.Left = 660; `$d.Top = 160
`$c = New-Object Windows.Forms.Button; `$c.Name = 'closeDialog'; `$c.Text = 'Close dialog'; `$c.Left = 20; `$c.Top = 20; `$c.Width = 120; `$c.DialogResult = 'OK'; `$d.Controls.Add(`$c)
`$o = New-Object Windows.Forms.Button; `$o.Name = 'openDialog'; `$o.Text = 'Open dialog'; `$o.Left = 160; `$o.Top = 60; `$o.Width = 120
`$o.Add_Click({ [void]`$f.BeginInvoke([Action]{ [void]`$d.ShowDialog(`$f) }) })
`$f.Controls.Add(`$t); `$f.Controls.Add(`$b); `$f.Controls.Add(`$o); `$f.Controls.Add(`$check); `$f.Controls.Add(`$tabs); `$f.Controls.Add(`$twinA); `$f.Controls.Add(`$twinB); `$f.Controls.Add(`$showCover); `$f.Controls.Add(`$hideCover); `$f.Controls.Add(`$list); `$f.Controls.Add(`$bigCover); `$f.Controls.Add(`$listCombo); `$f.Controls.Add(`$editCombo)
[Windows.Forms.Application]::Run(`$f)
"@
    $formFile = Join-Path $work 'form.ps1'; $formScript | Set-Content $formFile -Encoding UTF8   # too long for a command line
    $form = Start-Process powershell -ArgumentList '-NoProfile', '-STA', '-WindowStyle', 'Hidden', '-ExecutionPolicy', 'Bypass', '-File', "`"$formFile`"" -PassThru
    $w = AP -t local wait --window 'AP Verify Window' --timeout 20s
    Check 'wait --window sees the test window' ($w.Code -eq 0) $w.Out
    $win = AP -t local windows 'AP Verify'
    Check 'windows lists it with its pid' ($win.Out -match "\s$($form.Id)\s+powershell\.exe") $win.Out

    # Window objects on the managed heap. That process holds, per type (count / on screen / closed):
    #   EditorForm (derived from Form)  4 / 0 / 1   three never shown, one shown and closed again
    #   Form                            4 / 2 / 0   the form and the no-capture window on screen, the dialog and the cover window that are not open
    #   WPF Window                      4 / 1 / 1   two never shown, one on screen, one shown and closed again
    $r = AP -t local heap $form.Id --windows
    Check 'heap --windows: window objects per type, how many are shown and how many were closed' ($r.Code -eq 0 -and $r.Out -match '(?m)^\s*4\s+0\s+1\s+ApVerify\.Views\.EditorForm\s*$' -and $r.Out -match '(?m)^\s*4\s+2\s+0\s+System\.Windows\.Forms\.Form\s*$' -and $r.Out -match '(?m)^\s*4\s+1\s+1\s+System\.Windows\.Window\s*$' -and $r.Out -match '(?m)^CLOSED: ') $r.Out
    $r = AP -t local heap $form.Id --type Form --live --top 300
    Check 'heap --type looks at the type''s own name, not at its namespace' ($r.Out -match '(?m)^\s*4\s+\S+\s+ApVerify\.Views\.EditorForm\s*$' -and $r.Out -match '(?m)^\s*4\s+\S+\s+System\.Windows\.Forms\.Form\s*$' -and $r.Out -notmatch '(?m)System\.Windows\.Forms\.(Button|TextBox)\s*$') $r.Out
    $r = AP -t local heap $form.Id --type Node --ns ApVerify --live
    Check 'heap --type finds a type nested in a generic type by its own name' ($r.Out -match '(?m)^\s*5\s+\S+\s+ApVerify\.Views\.Cache<System\.Int32>\+Node\s*$' -and @($r.Out -split "`n").Count -eq 3) $r.Out
    $r = AP -t local heap $form.Id --type DisplayClass --live --top 1
    Check 'heap --type finds compiler-made types (their names begin with <)' ($r.Code -eq 0 -and $r.Out -match '(?m)^\s*[\d,]+\s+\S+\s+\S*<>c__DisplayClass') $r.Out
    $r = AP -t local heap $form.Id --type Forms.Button --live
    Check 'heap --type with a dot looks in the full name' ($r.Out -match '(?m)^\s*8\s+\S+\s+System\.Windows\.Forms\.Button\s*$') $r.Out
    $r = AP -t local heap $form.Id --ns ApVerify.Views --live
    Check 'heap --ns keeps the types of one namespace' ($r.Out -match '(?m)^\s*4\s+\S+\s+ApVerify\.Views\.EditorForm\s*$' -and $r.Out -match 'Cache<System\.Int32>\+Node' -and $r.Out -notmatch '(?m)\sSystem\.') $r.Out
    $r = AP -t local heap $form.Id --type 'ApVerify.*Form' --live
    Check 'heap --type with * is a pattern for the whole name' ($r.Out -match '(?m)^\s*4\s+\S+\s+ApVerify\.Views\.EditorForm\s*$' -and @($r.Out -split "`n").Count -eq 3) $r.Out
    $r = AP -t local heap $form.Id --type ApVerify --live
    Check 'heap --type that is only in a namespace: says so and names --ns' ($r.Out -match 'no reachable object of a type matching --type ApVerify' -and $r.Out -match "'ApVerify' is in \d+ type names, but not in the type's own name" -and $r.Out -match '--ns ApVerify') $r.Out
    $hinted = (AP -t local heap $form.Id --type '*ApVerify*' --live).Out
    Check 'heap: the pattern that hint suggests does list those types' ($hinted -match 'ApVerify\.Views\.EditorForm' -and $hinted -match 'Cache<System\.Int32>\+Node') $hinted

    $r = AP -t local heap $form.Id --type 'EditorForm,Node|NoSuchTypeZz' --ns ApVerify --live
    Check 'heap --type with several names: one snapshot, a row for each, and the one that matched nothing is named' ($r.Out -match '(?m)^\s*4\s+\S+\s+ApVerify\.Views\.EditorForm\s*$' -and $r.Out -match '(?m)^\s*5\s+\S+\s+ApVerify\.Views\.Cache<System\.Int32>\+Node\s*$' -and $r.Out -match '(?m)^no reachable object of a type matching: NoSuchTypeZz\s*$' -and @($r.Out -split "`n" | Where-Object { $_ -match 'snapshot taken' }).Count -eq 1) $r.Out
    $r = AP -t local heap $form.Id --type 'ApVerifyNope[,],EditorForm' --live
    Check 'heap --type: a comma inside [ ] is part of a name, not a separator (no flood of every array type)' ($r.Out -match '(?m)^\s*4\s+\S+\s+ApVerify\.Views\.EditorForm\s*$' -and $r.Out -match '(?m)^no reachable object of a type matching: ApVerifyNope\[,\]\s*$' -and $r.Out -notmatch '(?m)\sSystem\.Object\[\]\s*$') $r.Out
    $r = AP -t local heap $form.Id --show EditorForm --top 2
    $fieldLines = @('Number = 42', 'Note = "ap-note"', 'Flag = true', 'Where = \{System\.Drawing\.Point: x = 3, y = 4\}', 'Day = 3 \(System\.DayOfWeek\)', 'Nothing = null', 'Items = System\.Collections\.Generic\.List<System\.Int32>', 'Numbers = System\.Int32\[\] \(length 7\)', 'Auto = "auto-value"')
    $wrongFields = @($fieldLines | Where-Object { $r.Out -notmatch "(?m)^\s+$_\s*$" })
    Check 'heap --show: the values of an object''s fields (numbers, text, a struct, an enum, references)' ($r.Code -eq 0 -and $wrongFields.Count -eq 0 -and $r.Out -match "4 reachable objects of type 'EditorForm', the first 2 shown" -and $r.Out -match '(?m)^#2\s+ApVerify\.Views\.EditorForm') "not found: $($wrongFields -join ' | ')`n$($r.Out)"
    $r = AP -t local heap $form.Id --show EditorForm --fields 'Number,auto,noSuchFieldZz' --top 1
    Check 'heap --show --fields: only the named fields; one that does not exist is said to be missing' ($r.Out -match '(?m)^\s+Number = 42\s*$' -and $r.Out -match '(?m)^\s+Auto = "auto-value"\s*$' -and $r.Out -notmatch 'Note = ' -and $r.Out -match 'no field named noSuchFieldZz') $r.Out
    $r = AP -t local heap $form.Id --show NoSuchTypeForVerify
    Check 'heap --show for a type nothing reachable has: says so' ($r.Out -match "no reachable object of type 'NoSuchTypeForVerify'") $r.Out

    $shownHwnd = (AP -t local windows --hwnd 'AP Verify Window').Out.Trim()
    Check 'windows --hwnd prints the handle of that window' ((Test-Path $hwndFile) -and $shownHwnd -eq ('0x' + (Get-Content $hwndFile -Raw).Trim())) "got '$shownHwnd', the window says $(if (Test-Path $hwndFile) { Get-Content $hwndFile -Raw })"

    $tree = AP -t local ui 'AP Verify Window'
    Check 'ui: tree shows the button by name' ($tree.Out -match 'Button "Press me" #pressButton') $tree.Out
    # What the controls report about themselves: the check box is checked, the first tab is the selected one.
    Check 'ui: state of a check box, of the tabs and of the window' ($tree.Out -match '(?m)CheckBox "Check me" #checkBox \(checked\)' -and $tree.Out -match '(?m)TabItem "First tab" \(selected\)' -and $tree.Out -match '(?m)TabItem "Second tab"\s*$' -and $tree.Out -match '(?m)^Window "AP Verify Window" \(normal\)') $tree.Out
    $sl = (AP -t local ui get '#wpfSlider' --window 'ApWpf').Out
    Check 'ui: a slider gives its value with its range' ($sl -match '^Slider \[Slider\] #wpfSlider =0\.25 \(0\.\.1\)') $sl
    $ls = (AP -t local ui get '#longList' --window 'AP Verify Window').Out
    Check 'ui: a list that was scrolled says how far (item 31 of 60 on top is about half way)' ($ls -match '\(scroll v=(\d+(\.\d+)?)%\)' -and [double]$Matches[1] -gt 40 -and [double]$Matches[1] -lt 65 -and $ls -notmatch ' h=') $ls
    $pos = if ((AP -t local windows 'AP Verify Window').Out -match '(?m)\s(-?\d+,-?\d+ \d+x\d+)\s.*AP Verify Window') { $Matches[1] } else { 'no position' }
    Check 'ui: the window''s own line has the position and size that `windows` reports' ($tree.Out -match "(?m)^Window `"AP Verify Window`" \(normal\) $([regex]::Escape($pos))\s*`$") "windows: $pos / ui: $(($tree.Out -split "`n")[1])"
    $r = AP -t local ui 'AP Verify Window' --class --pos --grep pressButton
    Check 'ui --class adds each control''s class name' ($r.Out -match '(?m)^Button \[WindowsForms10\.BUTTON[^\]]*\] "Press me"') $r.Out
    if ($r.Out -match '@(\d+),(\d+)') {
        $r = AP -t local ui --at "@$($Matches[1]),$($Matches[2])"
        Check 'ui --at: the control at a point, what it lies in and their classes' ($r.Code -eq 0 -and $r.Out -match '(?m)^> +Button \[[^\]]+\] "Press me"' -and $r.Out -match '(?m)^Window \[[^\]]+\] "AP Verify Window"') $r.Out
    } else { Check 'ui: button position for --at' $false $r.Out }
    # Positions are left out unless asked for: an agent acts by name, and they are a quarter of the text.
    Check 'ui: no positions unless --pos asks for them' ($tree.Out -match '(?m)^\s*Button "Press me" #pressButton\s*$' -and $tree.Out -match '--pos adds positions' -and (AP -t local ui 'AP Verify Window' --pos).Out -match '(?m)Button "Press me" #pressButton @\d+,\d+\s*$') $tree.Out
    # A long run of items is cut to its first eight, and the line that follows says how many are not listed.
    $r = AP -t local ui 'AP Verify Window' --all
    $listed = @($r.Out -split "`n" | Where-Object { $_ -match '^\s*ListItem "item \d+"' }).Count
    Check 'ui: a long run of list items is folded, and a line says how many are left out' ($r.Out -match '(?m)^\s*\S (\d+) more ListItem here, not listed' -and ($listed + [int]$Matches[1]) -eq 60 -and $listed -eq 8) "$listed listed`n$($r.Out)"
    $r = AP -t local ui 'AP Verify Window' --all --items
    Check 'ui --items lists every item' (@($r.Out -split "`n" | Where-Object { $_ -match '^\s*ListItem "item \d+"' }).Count -eq 60 -and $r.Out -notmatch 'not listed') $r.Out
    $sr = AP -t local screenshot --scale 0.1
    $savedAt = if ($sr.Out -match '(?m)^saved: (.+\.png)\s*$') { $Matches[1].Trim() } else { '' }
    Check 'screenshot without -o goes to the temp folder, not into the current folder' ($savedAt.StartsWith([IO.Path]::GetTempPath(), [StringComparison]::OrdinalIgnoreCase) -and $savedAt -match '\\agentprobe\\screenshots\\' -and (Test-Path $savedAt) -and @(Get-ChildItem $work -Filter 'screenshot-*.png').Count -eq 0) $sr.Out
    if ($savedAt) { Remove-Item $savedAt -Force -ErrorAction SilentlyContinue }
    [void](AP -t local ui click 'Second tab' --window 'AP Verify Window'); [void](AP -t local ui click 'Check me' --window 'AP Verify Window'); Start-Sleep -Milliseconds 400
    $g1 = (AP -t local ui get 'Second tab' --window 'AP Verify Window').Out; $g2 = (AP -t local ui get 'First tab' --window 'AP Verify Window').Out; $g3 = (AP -t local ui get 'Check me' --window 'AP Verify Window').Out
    Check 'ui get: the state follows what was done (the other tab is selected, the box is unchecked)' ($g1 -match '^TabItem "Second tab" \(selected\)' -and $g2 -match '^TabItem "First tab" @' -and $g3 -match '\(unchecked\)') "$g1 / $g2 / $g3"
    Check 'ui get: a tab and its page have the same name; the tab is taken and the answer says there are two' ($g1 -match '\[2 controls have that name; this is the first') $g1

    # --grep looks at every control; --max limits the lines printed, and the total is stated.
    $r = AP -t local ui 'AP Verify Window' --grep 'Twin' --max 2
    Check 'ui --grep finds controls beyond the first --max of the tree' ($r.Out -match '2 controls match' -and @($r.Out -split "`n" | Where-Object { $_ -match '^Button "Twin"' }).Count -eq 2) $r.Out
    $r = AP -t local ui 'AP Verify Window' --grep 'Twin' --max 1
    Check 'ui --grep --max 1: one line, and it says that two match' ($r.Out -match '1 matching controls shown of 2 that match' -and @($r.Out -split "`n" | Where-Object { $_ -match '^Button "Twin"' }).Count -eq 1) $r.Out

    # Two buttons with the same name: the first is taken and the answer says there are two; --nth picks.
    Remove-Item $clickFile -ErrorAction SilentlyContinue
    $r = AP -t local ui click 'Twin' --window 'AP Verify Window'; Start-Sleep -Milliseconds 300
    Check 'ui click on a name two controls have: the first, and the answer says there are two' ($r.Out -match '\[2 controls have that name; this is the first' -and (Get-Content $clickFile -Raw -ErrorAction SilentlyContinue) -eq 'twin:A') "$($r.Out) / $(if (Test-Path $clickFile) { Get-Content $clickFile -Raw })"
    $r = AP -t local ui click 'Twin' --nth 2 --window 'AP Verify Window'; Start-Sleep -Milliseconds 300
    Check 'ui click --nth 2 takes the second' ($r.Code -eq 0 -and (Get-Content $clickFile -Raw -ErrorAction SilentlyContinue) -eq 'twin:B') "$($r.Out) / $(if (Test-Path $clickFile) { Get-Content $clickFile -Raw })"
    $r = AP -t local ui click 'Twin' --nth 3 --window 'AP Verify Window'
    Check 'ui click --nth 3 of two: an error, nothing pressed' ($r.Code -ne 0 -and $r.Out -match 'only 2 controls have the name') $r.Out

    # Another window in front of a control: what needs the mouse or a finger is refused and names that window.
    # (Nothing is clicked in these checks, so they also run on a PC someone is using.)
    Remove-Item $clickFile -ErrorAction SilentlyContinue
    [void](AP -t local ui click 'Show cover' --window 'AP Verify Window')
    $cw = AP -t local wait --window 'AP Verify Cover' --timeout 10s
    $twin = (AP -t local ui 'AP Verify Window' --pos --grep 'twinA').Out
    $coverAt = if ((AP -t local windows 'AP Verify Cover').Out -match '(?m)\s(-?\d+),(-?\d+) (\d+)x(\d+)\s.*AP Verify Cover') { @([int]$Matches[1], [int]$Matches[2], [int]$Matches[3], [int]$Matches[4]) } else { $null }
    $twinAt = if ($twin -match '@(\d+),(\d+)') { @([int]$Matches[1], [int]$Matches[2]) } else { $null }
    if ($cw.Code -eq 0 -and $coverAt -and $twinAt -and $twinAt[0] -ge $coverAt[0] -and $twinAt[0] -lt ($coverAt[0] + $coverAt[2]) -and $twinAt[1] -ge $coverAt[1] -and $twinAt[1] -lt ($coverAt[1] + $coverAt[3])) {
        $tx = $twinAt[0]; $ty = $twinAt[1]
        $r = AP -t local ui dblclick 'Twin' --window 'AP Verify Window'
        Check 'ui dblclick on a covered control: refused, the window in front is named' ($r.Code -ne 0 -and $r.Out -match 'is not on "AP Verify Window" \(powershell\.exe pid \d+\): "AP Verify Cover" \(powershell\.exe pid \d+\) is in front of it there; nothing was done' -and -not (Test-Path $clickFile)) "$($r.Code) $($r.Out)"
        $r = AP -t local ui tap 'Twin' --window 'AP Verify Window'
        Check 'ui tap on a covered control: refused the same way' ($r.Code -ne 0 -and $r.Out -match '"AP Verify Cover" .* is in front of it there; nothing was done' -and -not (Test-Path $clickFile)) "$($r.Code) $($r.Out)"
        $r = AP -t local click "@$tx,$ty" --window 'AP Verify Window'
        Check 'click --window: refused when another window is in front at that point' ($r.Code -ne 0 -and $r.Out -match '"AP Verify Cover" .* is in front of it there; nothing was done' -and $r.Out -match '--raise' -and -not (Test-Path $clickFile)) "$($r.Code) $($r.Out)"
        $r = AP -t local touch tap "@$tx,$ty" --window 'AP Verify Window'
        Check 'touch --window: refused when another window is in front at that point' ($r.Code -ne 0 -and $r.Out -match '"AP Verify Cover" .* is in front of it there; nothing was done' -and -not (Test-Path $clickFile)) "$($r.Code) $($r.Out)"
        $r = AP -t local click "@$tx,$ty" --window
        Check 'click --window without a title: an error, nothing clicked (not an unconditional click)' ($r.Code -ne 0 -and $r.Out -match '--window needs a title' -and -not (Test-Path $clickFile)) "$($r.Code) $($r.Out)"
        $r = AP -t local click "@$tx,$ty" --window 'No Such Window Zz'
        Check 'click --window with a title that matches nothing: an error, nothing clicked' ($r.Code -ne 0 -and $r.Out -match "no window matches 'No Such Window Zz'" -and -not (Test-Path $clickFile)) "$($r.Code) $($r.Out)"
    } else { Check 'the cover window for the "in front" checks lies over the button' $false "$($cw.Out) / cover at $($coverAt -join ',') / $twin" }
    [void](AP -t local ui click 'Hide cover' --window 'AP Verify Window')
    [void](AP -t local wait --no-window 'AP Verify Cover' --timeout 10s)

    # A window that lies wholly behind another one (a magenta one): `windows` marks it, a picture of the screen shows
    # the one in front, and `screenshot --window` still shows the window itself and says that it is covered.
    $press = (AP -t local ui 'AP Verify Window' --pos --grep pressButton).Out
    $origin = if ((AP -t local windows 'AP Verify Window').Out -match '(?m)\s(-?\d+),(-?\d+) \d+x\d+\s.*AP Verify Window') { @([int]$Matches[1], [int]$Matches[2]) } else { $null }
    [void](AP -t local ui click 'Big cover' --window 'AP Verify Window')
    $cw = AP -t local wait --window 'AP Verify Cover' --timeout 10s; Start-Sleep -Milliseconds 500
    if ($cw.Code -eq 0 -and $origin -and $press -match '@(\d+),(\d+)') {
        $bx = [int]$Matches[1] - $origin[0]; $by = [int]$Matches[2] - $origin[1]
        $win = (AP -t local windows 'AP Verify').Out
        Check 'windows marks a window that is behind another one as covered, and not the one in front' ($win -match '(?m)\scovered\s+AP Verify Window\s*$' -and $win -notmatch '(?m)covered\s+AP Verify Cover' -and $win -match '(?m)^covered = ') $win
        Add-Type -AssemblyName System.Drawing
        $own = Join-Path $work 'own.png'; $onScreen = Join-Path $work 'onscreen.png'
        $sr = AP -t local screenshot --window 'AP Verify Window' -o $own
        $sc = AP -t local screenshot --window 'AP Verify Cover' -o $onScreen
        $pixel = { param($file, $x, $y) $bmp = [Drawing.Bitmap]::FromFile($file); try { $c = $bmp.GetPixel($x, $y); "$($c.R),$($c.G),$($c.B)" } finally { $bmp.Dispose() } }
        $ownPixel = if (Test-Path $own) { & $pixel $own ($bx - 40) $by } else { 'no file' }
        $coverPixel = if (Test-Path $onScreen) { & $pixel $onScreen ($bx - 40) $by } else { 'no file' }
        Check 'screenshot --window shows the window itself although another window covers it, and says that it is covered' ($sr.Code -eq 0 -and $sr.Out -match 'ON THE SCREEN about \d+% of it is behind "AP Verify Cover"' -and $ownPixel -ne '255,0,255' -and $ownPixel -ne 'no file' -and $coverPixel -eq '255,0,255') "window: $ownPixel  cover: $coverPixel`n$($sr.Out)"
    } else { Check 'the full-size cover window appeared' $false "$($cw.Out) / $press" }
    [void](AP -t local ui click 'Hide cover' --window 'AP Verify Window')
    [void](AP -t local wait --no-window 'AP Verify Cover' --timeout 10s)
    [void](AP -t local ui click 'First tab' --window 'AP Verify Window')
    Check 'ui: tree shows the edit field by id' ($tree.Out -match 'Edit.*#inputBox') $tree.Out
    $s = AP -t local ui set '#inputBox' 'hello ui' --window 'AP Verify Window'
    Check 'ui set succeeds' ($s.Code -eq 0) $s.Out
    Check 'ui get reads the value back' ((AP -t local ui get '#inputBox' --window 'AP Verify Window').Out -match '=hello ui') ''

    # Saying what is expected instead of reading the state: one line, pass or fail.
    $r = AP -t local expect --ui '#inputBox' --has 'hello ui' --in 'AP Verify Window'
    Check 'expect --ui --has: one line and exit 0 when the control reads that' ($r.Code -eq 0 -and $r.Out -match "^ok: control '#inputBox' to read /hello ui/") "$($r.Code) $($r.Out)"
    $r = AP -t local expect --ui '#inputBox' --has 'something else' --in 'AP Verify Window' --timeout 1s
    Check 'expect that does not hold: exit 1, and what the control reads instead' ($r.Code -eq 1 -and $r.Out -match '^FAILED after 1s: .*\(it reads: Edit.* #inputBox =hello ui\)') "$($r.Code) $($r.Out)"
    $r = AP -t local wait --ui 'NoSuchControlZz' --in 'AP Verify Window' --timeout 1s
    Check 'wait --ui for a control that is not there: 124, and it says there is none' ($r.Code -eq 124 -and $r.Out -match "no control 'NoSuchControlZz' in window") "$($r.Code) $($r.Out)"
    $r = AP -t local expect --no-ui 'NoSuchControlZz' --in 'AP Verify Window'
    Check 'expect --no-ui holds for a control that is not there, and not for one that is' ($r.Code -eq 0 -and (AP -t local expect --no-ui 'Press me' --in 'AP Verify Window' --timeout 1s).Code -eq 1) "$($r.Code) $($r.Out)"
    $r = AP -t local expect --ui 'Press me' --in 'No Such Window Zz' --timeout 1s
    Check 'expect in a window that does not exist fails and says that (not "no such control")' ($r.Code -eq 1 -and $r.Out -match "no window matches 'No Such Window Zz'") "$($r.Code) $($r.Out)"

    # A list of commands as one command: quiet when all went well, the failed step when not.
    $stepsFile = Join-Path $work 'steps.txt'
    @('# a comment', 'ui set "#inputBox" "from steps" --window "AP Verify Window"', 'expect --ui "#inputBox" --has "from steps" --in "AP Verify Window"', 'try expect --ui "#inputBox" --has never --in "AP Verify Window" --timeout 1s', 'sleep 100ms', 'show ui get "#inputBox" --window "AP Verify Window"') | Set-Content $stepsFile -Encoding UTF8
    $r = AP -t local run $stepsFile
    Check 'run: every step ran; only the output of the `show` step and one line come back' ($r.Code -eq 0 -and $r.Out -match '(?m)^> ui get "#inputBox"' -and $r.Out -match '=from steps' -and $r.Out -match '(?m)^ok: 5 steps in .*failed but allowed to \(try\): step 3 exit 1' -and $r.Out -notmatch 'set Edit') "$($r.Code) $($r.Out)"
    @('expect --ui "#inputBox" --has nope --in "AP Verify Window" --timeout 1s', 'ui set "#inputBox" "must not run" --window "AP Verify Window"') | Set-Content $stepsFile -Encoding UTF8
    $r = AP -t local run $stepsFile --no-evidence
    Check 'run: stops at the step that fails, with its output and exit code; the rest is not run' ($r.Code -eq 1 -and $r.Out -match '(?m)^step 1 of 2 FAILED \(exit 1\): expect ' -and $r.Out -match 'it reads: Edit.*=from steps' -and $r.Out -match '1 step was not run' -and (AP -t local ui get '#inputBox' --window 'AP Verify Window').Out -match '=from steps') "$($r.Code) $($r.Out)"
    Remove-Item $clickFile -ErrorAction SilentlyContinue
    @('ui click "Press me" --window "AP Verify Window"', 'nosuchcommandzz foo') | Set-Content $stepsFile -Encoding UTF8
    $r = AP -t local run $stepsFile
    Start-Sleep -Milliseconds 300
    Check 'run: a line that is not a command means nothing is run' ($r.Code -eq 2 -and $r.Out -match "line 2: 'nosuchcommandzz' is not a command" -and $r.Out -match 'Nothing was run' -and -not (Test-Path $clickFile)) "$($r.Code) $($r.Out)"
    [void](AP -t local ui set '#inputBox' 'hello ui' --window 'AP Verify Window')
    $r = AP -t local ui click 'NoSuchControlZz' --window 'AP Verify Window'
    Check 'ui: "no such control" names the window that was looked in' ($r.Code -ne 0 -and $r.Out -match "no control named 'NoSuchControlZz' in window `"AP Verify Window`"") $r.Out
    $s = AP -t local ui set '#inputBox' 'read back' --window 'AP Verify Window'
    Check 'ui set says what the control holds afterwards' ($s.Code -eq 0 -and $s.Out -match 'now ="read back"') $s.Out
    [void](AP -t local ui set '#inputBox' 'hello ui' --window 'AP Verify Window')
    $s = AP -t local ui set '#editCombo' 'typed-in' --window 'AP Verify Window'
    $g = (AP -t local ui get '#editCombo' --window 'AP Verify Window').Out
    Check 'ui set on a combo box puts the text into it (its edit field), and the value is there afterwards' ($s.Code -eq 0 -and $g -match '=typed-in') "$($s.Out) / $g"
    $s = AP -t local ui set '#listCombo' 'NotInTheList' --window 'AP Verify Window'
    $g = (AP -t local ui get '#listCombo' --window 'AP Verify Window').Out
    Check 'ui set that a control does not take is an error, not a silent success' ($s.Code -ne 0 -and $g -match '=Alpha' -and $g -notmatch 'NotInTheList') "$($s.Code) $($s.Out) / $g"
    $c = AP -t local ui click 'Press me' --window 'AP Verify Window'
    Start-Sleep -Milliseconds 500
    Check 'ui click invoked the button (app saw the text)' ((Test-Path $clickFile) -and ((Get-Content $clickFile -Raw -Encoding UTF8) -eq 'clicked:hello ui')) "$($c.Out) / file: $(if (Test-Path $clickFile) { Get-Content $clickFile -Raw -Encoding UTF8 })"

    # An action says what it changed on the screen, so that no second command is needed to find out.
    Check 'ui click says so when nothing changed on the screen' ($c.Out -match '(?m)^no change seen within \d+(\.\d+)?s \(windows, and this window''s controls\)') $c.Out
    $s = AP -t local ui click 'Check me' --window 'AP Verify Window'
    Check 'ui click says which control reads differently afterwards, and what it read before' ($s.Out -match '(?m)^changed within \d' -and $s.Out -match '(?m)^\s+~ CheckBox "Check me" #checkBox \((un)?checked\)\s+\(was \((un)?checked\)\)\s*$') $s.Out
    $s = AP -t local ui click 'Check me' --window 'AP Verify Window' --no-watch
    Check 'ui click --no-watch leaves the look at the screen out' ($s.Code -eq 0 -and $s.Out -match 'CheckBox "Check me"' -and $s.Out -notmatch 'changed within|no change seen') $s.Out

    # raw input: focus, click by coordinates from the tree, select all, type, press the button with the keyboard
    if (-not $SkipInput) {
    Remove-Item $clickFile -ErrorAction SilentlyContinue
    [void](AP -t local focus 'AP Verify Window'); Start-Sleep -Milliseconds 300
    $edit = (AP -t local ui 'AP Verify Window' --pos --grep inputBox).Out
    if ($edit -match '@(\d+),(\d+)') {
        $ex = [int]$Matches[1]; $ey = [int]$Matches[2]
        # --hover moves the pointer and presses nothing
        Add-Type -AssemblyName System.Windows.Forms
        [void](AP -t local click ($ex + 15) ($ey + 2) --hover --screen); Start-Sleep -Milliseconds 200
        $at = [System.Windows.Forms.Cursor]::Position
        Check 'click --hover moves the pointer there without clicking' ([Math]::Abs($at.X - ($ex + 15)) -le 1 -and [Math]::Abs($at.Y - ($ey + 2)) -le 1 -and -not (Test-Path $clickFile)) "pointer at $($at.X),$($at.Y), asked for $($ex + 15),$($ey + 2)"
        $cr = AP -t local click $ex $ey --screen; Start-Sleep -Milliseconds 200
        Check 'click names the window that got the click' ($cr.Out -match "clicked at screen $ex,$ey on `"AP Verify Window`" \(powershell\.exe pid $($form.Id)\)") $cr.Out
        [void](AP -t local key ctrl+a); [void](AP -t local type 'typed 한글 a\nb'); Start-Sleep -Milliseconds 300
        Check 'click + type put the text in the field, backslashes untouched' ((AP -t local ui get '#inputBox' --window 'AP Verify Window').Out -match '=typed 한글 a\\nb') (AP -t local ui get '#inputBox' --window 'AP Verify Window').Out
        [void](AP -t local key tab); [void](AP -t local key space); Start-Sleep -Milliseconds 500
        Check 'key presses reached the app (button pressed with the keyboard)' ((Test-Path $clickFile) -and ((Get-Content $clickFile -Raw -Encoding UTF8) -eq 'clicked:typed 한글 a\nb')) "file: $(if (Test-Path $clickFile) { Get-Content $clickFile -Raw -Encoding UTF8 })"
    } else { Check 'ui: edit field position' $false $edit }

    # Touch: the app must see a finger, not the mouse (Windows marks the mouse messages it makes from touch).
    $press = (AP -t local ui 'AP Verify Window' --pos --grep pressButton).Out
    if ($press -match '@(\d+),(\d+)') {
        $px = $Matches[1]; $py = $Matches[2]
        Remove-Item $clickFile, $touchFile -ErrorAction SilentlyContinue
        [void](AP -t local click "@$px,$py"); Start-Sleep -Milliseconds 400
        Check 'the test window tells mouse from touch: a mouse click is seen as mouse, and it presses the button' ((Get-Content $touchFile -Raw -ErrorAction SilentlyContinue) -eq 'mouse' -and (Test-Path $clickFile)) "$(if (Test-Path $touchFile) { Get-Content $touchFile -Raw }) / pressed: $(Test-Path $clickFile)"
        # A WPF ToggleButton offers only "toggle", which would flip its state without the app's click handler running.
        Remove-Item $toggleFile -ErrorAction SilentlyContinue
        [void](AP -t local focus 'ApWpf'); Start-Sleep -Milliseconds 400
        $r = AP -t local ui click '#wpfToggle' --window 'ApWpf'; Start-Sleep -Milliseconds 500
        Check 'ui click on a button that can only be toggled uses the mouse, so the app''s click handler runs' ($r.Code -eq 0 -and $r.Out -match 'with the mouse \(a button that can only be toggled' -and (Get-Content $toggleFile -Raw -ErrorAction SilentlyContinue) -eq 'toggle-clicked') "$($r.Code) $($r.Out) / $(if (Test-Path $toggleFile) { Get-Content $toggleFile -Raw })"
        [void](AP -t local focus 'AP Verify Window'); Start-Sleep -Milliseconds 300
        Remove-Item $clickFile, $touchFile -ErrorAction SilentlyContinue
        $r = AP -t local touch tap "@$px,$py" --window 'AP Verify Window'; Start-Sleep -Milliseconds 600
        if ($r.Out -match 'Windows refused the touch input \(InitializeTouchInjection') {
            Write-Host "  skipped: this Windows does not accept injected touch input ($($r.Out.Trim()))." -ForegroundColor Yellow
        } else {
            Check 'touch tap presses the button, and the app sees touch input' ($r.Code -eq 0 -and $r.Out -match "tapped by touch at screen $px,$py on `"AP Verify Window`"" -and (Test-Path $clickFile) -and (Get-Content $touchFile -Raw -ErrorAction SilentlyContinue) -eq 'touch') "$($r.Code) $($r.Out) / pressed: $(Test-Path $clickFile) / seen as: $(if (Test-Path $touchFile) { Get-Content $touchFile -Raw })`n$((AP -t local windows 'AP Verify').Out)"
            # (The tap above is the first touch of this logon session when the suite runs on a fresh machine, and
            # it is sent with the look at the screen around it, so the process lives on after it: the case in
            # which a tap used to reach the button without pressing it.)
            # The same tap again, and once without the look at the screen: each must press the button.
            Remove-Item $clickFile, $touchFile -ErrorAction SilentlyContinue
            $r = AP -t local touch tap "@$px,$py" --window 'AP Verify Window'; Start-Sleep -Milliseconds 600
            Check 'touch tap a second time presses the button again' ($r.Code -eq 0 -and (Test-Path $clickFile) -and (Get-Content $touchFile -Raw -ErrorAction SilentlyContinue) -eq 'touch') "$($r.Code) $($r.Out) / pressed: $(Test-Path $clickFile) / seen as: $(if (Test-Path $touchFile) { Get-Content $touchFile -Raw })`n$((AP -t local windows 'AP Verify').Out)"
            Remove-Item $clickFile, $touchFile -ErrorAction SilentlyContinue
            $r = AP -t local touch tap "@$px,$py" --window 'AP Verify Window' --no-watch; Start-Sleep -Milliseconds 600
            Check 'touch tap --no-watch presses the button and says nothing about the screen' ($r.Code -eq 0 -and (Test-Path $clickFile) -and (Get-Content $touchFile -Raw -ErrorAction SilentlyContinue) -eq 'touch' -and $r.Out -notmatch 'changed within|no change seen') "$($r.Code) $($r.Out) / pressed: $(Test-Path $clickFile) / seen as: $(if (Test-Path $touchFile) { Get-Content $touchFile -Raw })"
            Remove-Item $clickFile, $touchFile -ErrorAction SilentlyContinue
            $r = AP -t local ui tap 'Press me' --window 'AP Verify Window'; Start-Sleep -Milliseconds 600
            Check 'ui tap taps a control by name, by touch' ($r.Code -eq 0 -and $r.Out -match 'tapped Button "Press me" by touch' -and (Test-Path $clickFile) -and (Get-Content $touchFile -Raw -ErrorAction SilentlyContinue) -eq 'touch') "$($r.Code) $($r.Out) / seen as: $(if (Test-Path $touchFile) { Get-Content $touchFile -Raw })"
            Remove-Item $clickFile, $touchFile, $tapsFile -ErrorAction SilentlyContinue
            $r = AP -t local ui dtap 'Press me' --window 'AP Verify Window'; Start-Sleep -Milliseconds 700
            Check 'ui dtap taps twice within one command (the app sees two touches)' ($r.Code -eq 0 -and $r.Out -match 'tapped twice Button "Press me" by touch' -and (Get-Content $tapsFile -Raw -ErrorAction SilentlyContinue) -eq 'xx' -and (Get-Content $touchFile -Raw -ErrorAction SilentlyContinue) -eq 'touch') "$($r.Code) $($r.Out) / taps: $(if (Test-Path $tapsFile) { Get-Content $tapsFile -Raw })"
            Remove-Item $clickFile, $touchFile -ErrorAction SilentlyContinue
            $r = AP -t local touch drag "@$px,$py" "@$([int]$px + 200),$([int]$py + 120)" --duration 200ms; Start-Sleep -Milliseconds 400
            Check 'touch drag that leaves the button does not press it' ($r.Code -eq 0 -and $r.Out -match 'dragged by touch' -and -not (Test-Path $clickFile)) "$($r.Code) $($r.Out)"
        }
        [void](AP -t local click "@$px,$([int]$py - 45)" --hover)   # the pointer comes back after touch input
    } else { Check 'ui: button position for the touch checks' $false $press }
    }

    # A modal dialog disables the window it belongs to: `windows` says so, and a click on that window names it.
    Remove-Item $clickFile -ErrorAction SilentlyContinue
    $od = AP -t local ui click 'Open dialog' --window 'AP Verify Window'
    Check 'ui click says which window opened, with what is in it' ($od.Out -match '(?m)^\s+\+ window "AP Verify Dialog" \(powershell\.exe pid \d+\) opened' -and $od.Out -match '(?m)^\s+Button "Close dialog" #closeDialog\s*$' -and @($od.Out -split "`n" | Where-Object { $_ -match 'Close dialog' }).Count -eq 1) $od.Out
    $dw = AP -t local wait --window 'AP Verify Dialog' --timeout 15s
    $win = (AP -t local windows 'AP Verify').Out
    Check 'windows: the owner of an open modal dialog is marked disabled, the dialog is not' ($dw.Code -eq 0 -and $win -match '(?m)\sdisabled\s+AP Verify Window\s*$' -and $win -notmatch '(?m)disabled\s+AP Verify Dialog' -and $win -match '(?m)^disabled = ') $win
    if (-not $SkipInput) {
        $press = (AP -t local ui 'AP Verify Window' --pos --grep pressButton).Out
        if ($press -match '@(\d+),(\d+)') {
            $r = AP -t local click "@$($Matches[1]),$($Matches[2])"
            Start-Sleep -Milliseconds 400
            Check 'click on a disabled window: says that it is disabled, and the app did not get the click' ($r.Out -match 'note: that point is on "AP Verify Window" \(powershell\.exe pid \d+\), which is disabled' -and -not (Test-Path $clickFile)) "$($r.Out) / clicked: $(Test-Path $clickFile)"
        } else { Check 'ui: button position while the dialog is open' $false $press }
    }
    $cd = AP -t local ui click 'Close dialog' --window 'AP Verify Dialog'
    Check 'ui click says which window closed' ($cd.Out -match '(?m)^\s+- window "AP Verify Dialog" \(powershell\.exe pid \d+\) closed') $cd.Out
    Start-Sleep -Milliseconds 700
    $win = (AP -t local windows 'AP Verify').Out
    Check 'windows: no longer disabled once the dialog is closed' ($win -match 'AP Verify Window' -and $win -notmatch 'disabled' -and $win -notmatch 'AP Verify Dialog') $win

    # A window that keeps itself out of screen captures is on the screen for the person, not in a screenshot:
    # `windows` marks it, and a screenshot of the area it is in names it.
    $kept = if (Test-Path $affinityFile) { (Get-Content $affinityFile -Raw).Trim() } else { 'none' }
    if ($kept -eq 'none') {
        Write-Host '  skipped: this Windows did not let the test window keep itself out of captures, so there is nothing to detect.' -ForegroundColor Yellow
    } else {
        $win = (AP -t local windows 'AP Verify').Out
        Check 'windows marks a window that keeps itself out of screen captures, and only that one' ($win -match '(?m)\sno-capture\s+AP Verify NoCapture\s*$' -and $win -notmatch '(?m)no-capture\s+AP Verify Window' -and $win -match '(?m)^no-capture = ') $win
        $full = Join-Path $work 'full.png'
        $sr = AP -t local screenshot --scale 0.25 -o $full
        Check 'screenshot names the window that is on the screen but not in the image, and how it came out' ($sr.Out -match "NOT in this image as the person at the PC sees it: .*`"AP Verify NoCapture`" \(powershell\.exe pid $($form.Id)\) at -?\d+,-?\d+ \d+x\d+ \($([regex]::Escape($(if ($kept -eq 'left out') { 'left out: what is behind it shows' } else { 'shown black' })))\)") $sr.Out
        $sr = AP -t local screenshot --window 'AP Verify Window' -o (Join-Path $work 'one.png')
        Check 'screenshot of an area without such a window says nothing of the kind' ($sr.Out -notmatch 'NOT in this image') $sr.Out
    }

    $shot = Join-Path $work 'shot.png'
    $sr = AP -t local screenshot --window 'AP Verify Window' -o $shot
    $okPng = (Test-Path $shot) -and (([IO.File]::ReadAllBytes($shot))[1..3] -join ',') -eq '80,78,71'
    Check 'screenshot of one window is a PNG' $okPng $sr.Out
    if ($okPng -and $sr.Out -match '(\d+)x(\d+) PNG') {
        $b = [IO.File]::ReadAllBytes($shot); $pw = [BitConverter]::ToUInt32($b[19..16], 0); $ph = [BitConverter]::ToUInt32($b[23..20], 0)
        Check 'screenshot size in the file matches what it reports' ($pw -eq [int]$Matches[1] -and $ph -eq [int]$Matches[2]) "file ${pw}x${ph}, reported $($Matches[1])x$($Matches[2])"
    }
    # A part of the picture (what an image costs to read goes by its pixels), and numbers instead of a picture.
    $part = Join-Path $work 'part.png'
    $sr = AP -t local screenshot --control 'Press me' --window 'AP Verify Window' -o $part
    if ((Test-Path $part) -and $sr.Out -match '(\d+)x(\d+) PNG of Button "Press me" #pressButton in window "AP Verify Window"') {
        $b = [IO.File]::ReadAllBytes($part); $cw = [BitConverter]::ToUInt32($b[19..16], 0); $ch = [BitConverter]::ToUInt32($b[23..20], 0)
        Check 'screenshot --control is that control only: the size it reports, a fraction of the window' ($cw -eq [int]$Matches[1] -and $ch -eq [int]$Matches[2] -and $pw -and $cw -lt ($pw / 2) -and $ch -lt ($ph / 4)) "control ${cw}x${ch}, window ${pw}x${ph}"
    } else { Check 'screenshot --control writes a picture of the control' $false $sr.Out }
    $none = Join-Path $work 'none.png'
    $sr = AP -t local screenshot --control 'NoSuchControlZz' --window 'AP Verify Window' -o $none
    Check 'screenshot --control for a control that is not there: an error and no picture' ($sr.Code -ne 0 -and $sr.Out -match "no control 'NoSuchControlZz'" -and -not (Test-Path $none)) "$($sr.Code) $($sr.Out)"
    $sr = AP -t local screenshot --region '999999,999999,10,10' -o $none
    Check 'screenshot --region outside the screen: an error and no picture' ($sr.Code -ne 0 -and $sr.Out -match 'lies outside' -and -not (Test-Path $none)) "$($sr.Code) $($sr.Out)"
    [void](AP -t local ui click 'Show cover' --window 'AP Verify Window' --no-watch)
    if ((AP -t local wait --window 'AP Verify Cover' --timeout 10s).Code -eq 0) {
        Start-Sleep -Milliseconds 500
        $sr = AP -t local screenshot --window 'AP Verify Cover' --stats -o $none
        Check 'screenshot --stats: numbers and no picture; a magenta window is mostly magenta' ($sr.Code -eq 0 -and $sr.Out -match 'no image sent: average color #[0-9A-F]{6}; .* the most common color, about #F808F8, covers (\d+(\.\d+)?)%' -and [double]$Matches[1] -gt 40 -and $sr.Out -notmatch 'saved:' -and -not (Test-Path $none)) "$($sr.Code) $($sr.Out)"
    } else { Check 'the cover window for the --stats check appeared' $false '' }
    [void](AP -t local ui click 'Hide cover' --window 'AP Verify Window' --no-watch)
    [void](AP -t local wait --no-window 'AP Verify Cover' --timeout 10s)
    $sr = AP -t local screenshot --window 'AP Verify Window' --diff -o $none
    Check 'screenshot --diff needs a session (the picture before is kept on the target): -t local says so' ($sr.Code -eq 2 -and $sr.Out -match 'keeps state between calls' -and -not (Test-Path $none)) "$($sr.Code) $($sr.Out)"

    # The guide: a short core that stays in an agent's context, and topics that are read when needed.
    $core = AP guide; $all = AP guide all; $topic = AP guide ui
    Check 'guide: the core is short and lists the topics; a topic is that part; all is everything' ($core.Code -eq 0 -and $core.Out.Length -lt ($all.Out.Length / 2) -and $core.Out -match '(?m)^\s+guide ui\s' -and $topic.Out -match '^## Driving the UI' -and $all.Out.Contains($topic.Out.Trim()) -and ($core.Out + $all.Out) -notmatch '<!-- topic') "core $($core.Out.Length) characters, all $($all.Out.Length)"
    $r = AP guide nosuchtopic
    Check 'guide: an unknown topic is an error that lists the topics' ($r.Code -eq 2 -and $r.Out -match "no topic 'nosuchtopic'" -and $r.Out -match 'guide ui') "$($r.Code) $($r.Out)"

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
            $connectedAt = Get-Date
            Check 'a local target connects by discovery' $up (AP list).Out
            if ($up) {
                # STATE is about commands (how long ago the last one ended), CONNECTED about the connection.
                [void](AP exec 'Write-Output warm'); Start-Sleep 3
                $listed = (AP list).Out
                Check 'list: STATE is the time since the last command ended, CONNECTED the time the PC connected' ($listed -match '(?m)^verify\s.*\sidle (\d+(\.\d+)?)s\s+(\d\d:\d\d:\d\d)\s' -and [double]$Matches[1] -ge 2.5 -and [double]$Matches[1] -lt 30 -and [Math]::Abs(((Get-Date $Matches[3]) - $connectedAt).TotalSeconds) -lt 40) $listed

                # A second PC connects in the middle of work that never named its target: that work keeps its target.
                $env:AGENTPROBE_INSTANCE = 'verify2'   # another share on this PC; it finds the same developer PC (same port)
                $second = Start-Process $Exe -ArgumentList 'share', '--trust', $myKey, '--name', 'second' -WindowStyle Minimized -PassThru
                $env:AGENTPROBE_INSTANCE = 'verify'
                $two = $false; 1..30 | ForEach-Object { if (-not $two) { Start-Sleep -Milliseconds 700; if ((AP list).Out -match '(?m)^second\s') { $two = $true } } }
                if (-not $two) { Check 'a second target connects to the same developer PC' $false (AP list).Out }
                else {
                    $r = AP exec 'Write-Output "ran-on-$env:AGENTPROBE_INSTANCE"'
                    Check 'a command without -t keeps going to the target in use when another PC connects, and says so' ($r.Code -eq 0 -and $r.Out -match '(?m)^ran-on-verify$' -and $r.Out -match '2 targets are connected and none was named: this went to verify' -and $r.Out -match 'Also connected: second \(connected ') "$($r.Code) $($r.Out)"
                    $env:AGENTPROBE_TARGET = 'second'
                    $r = AP exec 'Write-Output "ran-on-$env:AGENTPROBE_INSTANCE"'
                    Remove-Item Env:AGENTPROBE_TARGET
                    Check 'AGENTPROBE_TARGET names the target for commands without -t' ($r.Code -eq 0 -and $r.Out -match '(?m)^ran-on-verify2$' -and $r.Out -notmatch 'none was named') "$($r.Code) $($r.Out)"
                    [void](AP -t second end)
                    1..20 | ForEach-Object { if ((AP list).Out -match '(?m)^second\s') { Start-Sleep -Milliseconds 500 } }
                }
                if (-not $second.HasExited) { Stop-Process -Id $second.Id -Force -ErrorAction SilentlyContinue }

                # The person at the target presses the space bar in agentprobe's window: commands are refused until Enter is pressed there.
                # (That window gets a real key press, so this is left out on a PC someone is using.)
                if (-not $SkipInput) {
                    $shareTitle = @('sharing this PC', '이 PC 공유 중') | Where-Object { (AP -t local windows $_).Out -match 'agentprobe - ' } | Select-Object -First 1
                    if ($shareTitle) { [void](AP -t local focus $shareTitle); Start-Sleep -Milliseconds 700 }
                    if (-not $shareTitle -or (AP -t local windows $shareTitle).Out -notmatch '(?m)\sforeground\b') {
                        Write-Host '  skipped: the sharing window could not be brought to the front here, so the space bar cannot be pressed in it.' -ForegroundColor Yellow
                    } else {
                        [void](AP -t local key space)
                        $pausedSeen = $false; 1..24 | ForEach-Object { if (-not $pausedSeen) { Start-Sleep -Milliseconds 500; $pausedSeen = (AP list).Out -match '(?m)^verify\s.*\spaused idle ' } }
                        $r = AP -t verify exec 'Write-Output should-not-run'
                        Check 'the space bar in the sharing window pauses: the next command is refused (exit 5) and says why; list shows paused' ($pausedSeen -and $r.Code -eq 5 -and $r.Out -match 'paused by the person at the target' -and $r.Out -notmatch '(?m)^should-not-run') "$pausedSeen / $($r.Code) $($r.Out)`n$((AP list).Out)"
                        [void](AP -t local focus $shareTitle); Start-Sleep -Milliseconds 500; [void](AP -t local key space)   # a second space bar must not undo the pause
                        Start-Sleep 1; $still = AP -t verify exec 'Write-Output should-not-run-either'
                        Check 'pressing the space bar again does not resume (a key held too long must not undo the pause)' ($still.Code -eq 5) "$($still.Code) $($still.Out)"
                        $pr = AP -t verify push $file 'paused.txt'
                        Check 'a push while paused is refused with the same exit code' ($pr.Code -eq 5 -and $pr.Out -match 'paused by the person at the target') "$($pr.Code) $($pr.Out)"
                        [void](AP -t local focus $shareTitle); Start-Sleep -Milliseconds 500; [void](AP -t local key enter)
                        $r = $null; 1..10 | ForEach-Object { if (-not $r -or $r.Code -ne 0) { Start-Sleep -Milliseconds 400; $r = AP -t verify exec 'Write-Output runs-again' } }
                        Check 'Enter resumes: commands run' ($r.Code -eq 0 -and $r.Out -match 'runs-again') "$($r.Code) $($r.Out)"
                    }
                }

                # A PC with several adapters: the list also gives the addresses it does not connect from.
                $listed = (AP list).Out
                $from = if ($listed -match '(?m)^verify\s+\S+\s+\S+\s+(\d+\.\d+\.\d+\.\d+)\s') { $Matches[1] } else { '' }
                $upIf = @(Get-NetAdapter | Where-Object Status -eq 'Up' | ForEach-Object { $_.ifIndex })
                $own = @(Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $upIf -contains $_.InterfaceIndex -and $_.IPAddress -notlike '169.254.*' } | ForEach-Object { $_.IPAddress })
                $others = if ($listed -match '(?m)^ADDRESS is the address each PC connects from\. Other addresses it reports having: verify ([\d., ]+)') { @($Matches[1].Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ }) } else { @() }
                $expectOthers = @($own | Where-Object { $_ -ne $from })
                if ($expectOthers.Count -eq 0) { Check 'list: a PC with one address has no "other addresses" line' ($others.Count -eq 0) $listed }
                else { Check 'list: the other addresses of a PC are ones Windows reports for it, and not the one it connects from' ($others.Count -ge 1 -and @($others | Where-Object { $own -notcontains $_ -or $_ -eq $from }).Count -eq 0) "listed: $($others -join ' ')  Windows: $($own -join ' ')  connects from: $from`n$listed" }
            }
            if ($up) {
                $dry = AP share --dry-run --lang en
                Check 'share --dry-run: the developer PC that answers is listed with its key, and nothing is shared' ($dry.Code -eq 0 -and $dry.Out -match "(?m)^\s+$([regex]::Escape($env:COMPUTERNAME))\s+\(user .+\)\s+key $myKey\s+at [\d., ]+$" -and @((AP list).Out -split "`n" | Where-Object { $_ -match '\sidle\s|\sbusy' }).Count -eq 1) $dry.Out

                # The start screen as text (piped): the link, the MCP command with this exe's path, the connected PC.
                $homeText = (AP home).Out
                Check 'home: prints the dashboard link, the MCP command for this exe and the connected PC' ($homeText -match 'http://127\.0\.0\.1:\d+/\?k=[0-9A-F]{16,}' -and $homeText.Contains("claude mcp add agentprobe -- `"$Exe`" mcp") -and $homeText -match '(?m):\s.*\bverify\b.*$') $homeText
                Check 'home: the first thing to give an agent is the prompt for the chat; no line for an instructions file' ($homeText -match '(?m)^\s+1\. .*\n\s+.*guide.*list' -and $homeText -notmatch 'CLAUDE\.md|AGENTS\.md') $homeText
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
                Check 'read: 120000 log lines that differ only in a number come back as the first, the count of the rest and the last' ($r.Code -eq 0 -and @($r.Out -split "`n").Count -eq 2 -and $r.Out -match 'request-id=1 ok' -and $r.Out -match '\[\+119999 more lines like the one above that differ only in numbers; the last one: .*request-id=120000 ok') ($r.Out.Substring(0, [Math]::Min(400, $r.Out.Length)))
                $r = AP -t verify read $big --head 120000 --no-fold
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
                $r = AP -t verify exec '1..20 | ForEach-Object { Get-Item "C:\no-such-path-for-verify-$_" }; Write-Output done'
                Check 'exec: the same error from the same place is printed once, with how many more came and the last one' ($r.Out -match '(?m)^done$' -and ([regex]::Matches($r.Out, 'FullyQualifiedErrorId')).Count -eq 1 -and $r.Out -match 'came 19 more times \(x20 in all\); the last one: .*no-such-path-for-verify-20') $r.Out
                $r = AP -t verify exec --no-fold '1..20 | ForEach-Object { Get-Item "C:\no-such-path-for-verify-$_" }'
                Check 'exec --no-fold prints each error' (([regex]::Matches($r.Out, 'FullyQualifiedErrorId')).Count -eq 20) $r.Out
                $before = (Get-ItemProperty HKLM:\SYSTEM\CurrentControlSet\Control\CrashControl).AutoReboot
                $r = AP -t verify crashdumps --no-autoreboot
                if (([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
                    $set = (Get-ItemProperty HKLM:\SYSTEM\CurrentControlSet\Control\CrashControl).AutoReboot
                    $status = (AP -t verify crashdumps).Out
                    [void](AP -t verify crashdumps off)
                    $after = (Get-ItemProperty HKLM:\SYSTEM\CurrentControlSet\Control\CrashControl).AutoReboot
                    Check 'crashdumps --no-autoreboot: AutoReboot=0 while it is on, as it was after off' ($r.Code -eq 0 -and ($before -eq 0 -or ($set -eq 0 -and $status -match 'stays on it \(AutoReboot=0, set by agentprobe\)')) -and $after -eq $before) "before $before, set $set, after ${after}: $($r.Out) / $status"
                } else {
                    Check 'crashdumps --no-autoreboot: refused without administrator rights, and nothing changed' ($r.Code -eq 2 -and $r.Out -match 'needs agentprobe elevated' -and (Get-ItemProperty HKLM:\SYSTEM\CurrentControlSet\Control\CrashControl).AutoReboot -eq $before) $r.Out
                }
                $r = AP -t verify exec --timeout 2s 'Start-Sleep 30'
                Check 'exec --timeout returns 124' ($r.Code -eq 124) "$($r.Code) $($r.Out)"
                $r = AP -t verify exec '1..1000 | ForEach-Object { "row $_" }'
                $saved = if ($r.Out -match 'Full output \((\d+) lines\): (.+?\.txt)') { $Matches[2] } else { $null }
                Check 'long output is cut and says so' ($r.Out -match 'lines omitted' -and $r.Out -match 'row 1\b' -and $r.Out -match 'row 1000') ''
                Check 'the saved full output has every line' ($saved -and (Get-Content $saved).Count -eq 1000) "saved: $saved"
                $r = AP -t verify exec --full '1..1000 | ForEach-Object { "row $_" }'
                Check 'exec --full prints everything' (($r.Out -split "`n" | Where-Object { $_ -match '^row \d+$' }).Count -eq 1000) ''

                # What is printed has a budget in characters too; a line repeated in a row is printed once; and
                # --quiet-ok keeps a command that went well to its last lines.
                $r = AP -t verify exec '1..150 | ForEach-Object { "$_ " + ("y" * 300) }'
                Check 'long lines: output is cut by its size although it has fewer than 200 lines' ($r.Out -match '\[\.\.\. (\d+) lines omitted\. Full output \(150 lines\)' -and [int]$Matches[1] -gt 30 -and $r.Out -match '(?m)^1 y' -and $r.Out -match '(?m)^150 y') ($r.Out.Substring(0, [Math]::Min(300, $r.Out.Length)))
                $r = AP -t verify exec '1..6 | ForEach-Object { "same line" }; "other"'
                Check 'a line that comes several times in a row is printed once, with how often' (($r.Out -split "`n")[0] -eq 'same line  [x6: this line came 6 times in a row]' -and $r.Out -match '(?m)^other$') $r.Out
                $r = AP -t verify exec --quiet-ok '1..400 | ForEach-Object { "build line $_" }; "Build succeeded."'
                $savedQ = if ($r.Out -match 'Full output \(401 lines\): (.+?\.txt)') { $Matches[1] } else { $null }
                Check 'exec --quiet-ok: a command that went well prints its last lines and where the rest is' ($r.Code -eq 0 -and $r.Out -match '396 earlier lines not shown' -and $r.Out -match '(?m)^Build succeeded\.$' -and $r.Out -notmatch '(?m)^build line 1$' -and $savedQ -and (Get-Content $savedQ).Count -eq 401) $r.Out
                $r = AP -t verify exec --quiet-ok '1..3 | ForEach-Object { "line $_" }; exit 4'
                Check 'exec --quiet-ok: a command that failed prints its output as usual' ($r.Code -eq 4 -and $r.Out -match '(?m)^line 1$' -and $r.Out -match '(?m)^line 3$') "$($r.Code) $($r.Out)"

                # Log lines that differ only in their numbers are folded, and the line says so; other lines never are.
                [void](AP -t verify exec '$l = @("12:00:00 start"); 1..50 | ForEach-Object { $l += ("12:00:{0:00} ERROR retry {1} failed" -f $_, $_) }; $l += "12:01:00 done"; $l += 1..6 | ForEach-Object { "value$_=$_" }; $l | Set-Content fold.log')
                $r = AP -t verify read fold.log
                Check 'read: log lines that differ only in numbers are the first one and a line that counts the rest and gives the last' ($r.Out -match '(?m)^12:00:01 ERROR retry 1 failed$' -and $r.Out -match '\[\+49 more lines like the one above that differ only in numbers; the last one: 12:00:50 ERROR retry 50 failed' -and @($r.Out -split "`n" | Where-Object { $_ -match '^value\d=\d$' }).Count -eq 6 -and @($r.Out -split "`n").Count -eq 10) $r.Out
                Check 'read --no-fold prints every line' (@((AP -t verify read fold.log --no-fold).Out -split "`n").Count -eq 58) ''

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
                $r = AP -t verify exec '@(Get-ChildItem -Filter script-*.*).Count'
                Check 'the script files exec -f ran from are gone from the workspace afterwards' ($r.Out.Trim() -eq '0') $r.Out

                # A GUI app is tracked as a job only when it is started itself, not through a shell.
                $gui = "$env:WINDIR\System32\winver.exe"   # a plain GUI program that every Windows has, server editions included
                $r = AP -t verify start --name gui --exe $gui
                $guiPid = if ($r.Out -match 'job \d+ started \(pid (\d+)\)') { [int]$Matches[1] } else { 0 }
                $guiProc = if ($guiPid) { Get-Process -Id $guiPid -ErrorAction SilentlyContinue }
                Check 'start --exe: the job is the app itself (its pid, still running)' ($guiProc -and $guiProc.ProcessName -eq 'winver' -and (AP -t verify jobs).Out -match "(?m)^\s*\d+\s+gui\s+$guiPid\s+running") "$($r.Out)`n$((AP -t verify jobs).Out)"
                [void](AP -t verify kill gui); Start-Sleep 1
                Check 'kill <job> closes an app started with --exe, and jobs shows its exit' (-not (Get-Process -Id $guiPid -ErrorAction SilentlyContinue) -and (AP -t verify jobs).Out -match "(?m)^\s*\d+\s+gui\s+$guiPid\s+exit ") (AP -t verify jobs).Out

                # Swapping a running app for another start of it: the running one is closed by its window, not killed.
                $r = AP -t verify start --name old --exe $gui
                $oldPid = if ($r.Out -match 'job \d+ started \(pid (\d+)\)') { [int]$Matches[1] } else { 0 }
                Start-Sleep 2   # its window is up
                $r = AP -t verify start --name new --replace winver.exe --exe $gui
                $newPid = if ($r.Out -match 'job \d+ started \(pid (\d+)\)') { [int]$Matches[1] } else { 0 }
                Check 'start --replace closes the running instance by its window, waits for it, then starts' ($oldPid -and $newPid -and $newPid -ne $oldPid -and $r.Out -match "closed winver\.exe \(pid $oldPid\) by closing its window" -and -not (Get-Process -Id $oldPid -ErrorAction SilentlyContinue) -and (Get-Process -Id $newPid -ErrorAction SilentlyContinue) -and (AP -t verify jobs).Out -match "(?m)^\s*\d+\s+old\s+$oldPid\s+exit 0\b") "$($r.Out)`n$((AP -t verify jobs).Out)"
                [void](AP -t verify kill new); Start-Sleep 1
                $r = AP -t verify start --name none --replace NoSuchAppForVerifyZz.exe --exe $gui
                Check 'start --replace with nothing to close says so and starts' ($r.Out -match 'no NoSuchAppForVerifyZz\.exe was running' -and $r.Out -match 'job \d+ started') $r.Out
                [void](AP -t verify kill none); Start-Sleep 1

                # A script that launches a program and ends: what that program writes later is not the command's output.
                $childScript = Join-Path $work 'child.ps1'
                @'
$psi = New-Object System.Diagnostics.ProcessStartInfo 'cmd.exe', '/c ping -n 9 127.0.0.1 >nul & echo late-child-output'
$psi.UseShellExecute = $false
[void][System.Diagnostics.Process]::Start($psi)
Write-Output 'script-done'
'@ | Set-Content $childScript -Encoding UTF8
                $sw = [Diagnostics.Stopwatch]::StartNew(); $r = AP -t verify exec -f $childScript; $sw.Stop()
                Check 'exec ends with the script: a program it left running is named, and its later output is not taken' ($r.Code -eq 0 -and $r.Out -match '(?m)^script-done$' -and $r.Out -notmatch 'late-child-output' -and $r.Out -match 'cmd\.exe\(\d+\), which it started, is still running and writes to the same output' -and $sw.Elapsed.TotalSeconds -lt 6) "$($r.Code) after $([int]$sw.Elapsed.TotalSeconds)s: $($r.Out)"
                $r = AP -t verify start --name viashell "& '$gui'"
                Check 'start <command> that only launches a GUI app says the job is not the app' ($r.Out -match 'already ended' -and $r.Out -match 'start --exe') $r.Out
                [void](AP -t verify kill --name winver.exe)

                # The folder a job starts in is part of the answer, and a job can be given environment variables.
                $r = AP -t verify start --name cwdjob --exe $gui
                Check 'start --exe names the folder the program started in (its own, by default)' ($r.Out -match "job \d+ started \(pid \d+\): winver\.exe\s+cwd=$([regex]::Escape("$env:WINDIR\System32"))\s*$") $r.Out
                [void](AP -t verify kill cwdjob)
                $r = AP -t verify start --name envjob --cwd $work --env AP_VERIFY_ONE=first --env 'AP_VERIFY_TWO=two words' 'Write-Output "$env:AP_VERIFY_ONE/$env:AP_VERIFY_TWO/$((Get-Location).Path)"; Start-Sleep 20'
                Start-Sleep 3
                $lg = (AP -t verify logs envjob).Out
                Check 'start --env and --cwd reach the job, and the answer shows them' ($r.Out -match "cwd=$([regex]::Escape($work))" -and $r.Out -match 'env: AP_VERIFY_ONE=first AP_VERIFY_TWO=two words' -and $lg -match "(?m)^first/two words/$([regex]::Escape($work))\s*$") "$($r.Out)`n$lg"
                [void](AP -t verify kill envjob)
                Check 'start --env without NAME=VALUE is an error' ((AP -t verify start --env NOVALUE 'Write-Output x').Code -eq 2) ''

                # A shortcut is started as the person at the PC starts it: its target, its arguments, its start folder.
                $r = AP -t verify start --name lnkjob --lnk $lnk
                $lnkPid = if ($r.Out -match 'job \d+ started \(pid (\d+)\): winver\.exe\s+cwd=(\S+)') { [int]$Matches[1] } else { 0 }
                $lnkProc = if ($lnkPid) { Get-CimInstance Win32_Process -Filter "ProcessId=$lnkPid" }
                Check 'start --lnk starts the shortcut''s target with its arguments in its start folder' ($lnkProc -and $lnkProc.Name -eq 'winver.exe' -and $lnkProc.CommandLine -match '--alpha "two words"\s*$' -and $r.Out -match "cwd=$([regex]::Escape($env:WINDIR)) \(the shortcut's `"Start in`"\)" -and $r.Out -match 'shortcut target .*winver\.exe\s+args: --alpha "two words"') "$($r.Out)`n$($lnkProc.CommandLine)"
                [void](AP -t verify kill lnkjob); Start-Sleep 1

                # An app that is not running: triage finds it by its Start menu shortcut and says how to start it.
                $menuLnk = Join-Path ([Environment]::GetFolderPath('Programs')) 'ApVerify Probe App.lnk'
                try {
                    Copy-Item $lnk $menuLnk
                    $unLnk = Join-Path ([Environment]::GetFolderPath('Programs')) 'Uninstall ApVerify Probe App.lnk'
                    $sc2 = $shell.CreateShortcut($unLnk); $sc2.TargetPath = "$env:WINDIR\System32\where.exe"; $sc2.Save()
                    $tr = (AP -t verify triage ApVerifyProbe).Out
                    Check 'triage for an app that is not running: where it may be installed and how to start it' ($tr -match "(?m)^process\s+'ApVerifyProbe' is NOT running" -and $tr -match "(?m)^\s+maybe\s+$([regex]::Escape("$env:WINDIR\System32\winver.exe"))\s+\(shortcut\)" -and $tr -match "start --lnk `"$([regex]::Escape($menuLnk))`"" -and $tr -match 'If winver\.exe above is the app') $tr
                    Check 'triage does not offer an uninstaller as the app' ($tr -notmatch 'Uninstall ApVerify' -and $tr -notmatch 'where\.exe') $tr
                } finally { Remove-Item $menuLnk, $unLnk -Force -ErrorAction SilentlyContinue }
                $tr = (AP -t verify triage NoSuchAppForVerifyZz).Out
                Check 'triage for a name nothing is installed under says that nothing was found' ($tr -match "'NoSuchAppForVerifyZz' is not running, and nothing by that name was found") $tr

                # Exit codes of the .NET host are named like crash codes.
                $r = AP -t verify exec --shell cmd 'exit /b -2147450751'
                Check 'exec: an exit code of the .NET host is named' ($r.Code -eq -2147450751 -and $r.Out -match '\[exit code 0x80008081 \.NET host: InvalidArgFailure') "$($r.Code) $($r.Out)"
                $r = AP -t verify deps "$env:WINDIR\System32\where.exe" --run --wait 5s '--' /?
                Check 'deps --run on a program without a .NET host: says there is no host trace, and gives the exit code' ($r.Out -match 'no host trace was written' -and $r.Out -match '(?m)^result\s+the program ended after .* with exit 0\s*$') "$($r.Code) $($r.Out)"
                $r = AP -t verify deps "$env:WINDIR\System32\where.exe" --run --wait 5s '--' -o ap-verify-arg --full   # quoted: PowerShell would take a bare -- for itself
                $activity = Get-Content (Join-Path (Split-Path $Exe) 'agentprobe-activity.log') -Tail 200 -Encoding UTF8 -ErrorAction SilentlyContinue
                Check 'deps --run: what follows -- goes to the program, not to agentprobe' ($r.Out -match 'args: -o ap-verify-arg --full') $r.Out
                Check 'deps --run is in the activity log with the program and its arguments, like any command that starts something' (@($activity | Where-Object { $_ -match '#\d+ deps .*where\.exe --run .*-- -o ap-verify-arg' }).Count -ge 1 -and @($activity | Where-Object { $_ -match 'deps --run: started .*where\.exe -o ap-verify-arg --full in ' }).Count -ge 1) (($activity | Select-Object -Last 6) -join "`n")
                $r = AP -t verify read $big --new --between 12:00 13:00
                Check 'read --between with --new is refused, not answered about lines that were not read' ($r.Code -eq 2 -and $r.Out -match 'does not combine with --new') "$($r.Code) $($r.Out)"

                # A real self-contained .NET app, when this PC can build one: what agentprobe says about a missing
                # deps.json is what the .NET host really does (ends with 0x80008081 from the app's own folder only).
                $sdk = if (Get-Command dotnet -ErrorAction SilentlyContinue) { (& dotnet --version 2>$null | Select-Object -First 1) }
                $built = $false
                if ("$sdk" -match '^(\d+)\.') {
                    # The .NET 11 host no longer has the trap these checks are about: the app is built for .NET 10 at most.
                    $tfm = "net$([Math]::Min([int]$Matches[1], 10)).0"
                    $src = Join-Path $work 'hostapp-src'; $app = Join-Path $work 'hostapp'; New-Item -ItemType Directory $src | Out-Null
                    "<Project Sdk=`"Microsoft.NET.Sdk`"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>$tfm</TargetFramework><RuntimeIdentifier>win-x64</RuntimeIdentifier><SelfContained>true</SelfContained><AssemblyName>ApHostApp</AssemblyName></PropertyGroup></Project>" | Set-Content "$src\hostapp.csproj"
                    'System.Console.WriteLine("ap-host-app ran"); return 7;' | Set-Content "$src\Program.cs"
                    & dotnet publish "$src\hostapp.csproj" -c Release -o $app --nologo -v q 2>&1 | Out-Null
                    $built = (Test-Path "$app\ApHostApp.exe") -and (Test-Path "$app\ApHostApp.deps.json")
                }
                if (-not $built) {
                    Write-Host '  skipped: no .NET SDK that can publish a self-contained app here, so the .NET host checks run against no real host.' -ForegroundColor Yellow
                } else {
                    $r = AP -t verify deps "$app\ApHostApp.exe"
                    Check 'deps: a complete self-contained .NET app has no problems' ($r.Code -eq 0 -and $r.Out -match 'ApHostApp\.deps\.json lists \d+ files, all in the folder') $r.Out
                    $r = AP -t verify deps "$app\ApHostApp.exe" --run
                    Check 'deps --run: the host runs it as an app, starts the runtime, and the app''s own exit code comes back' ($r.Code -eq 0 -and $r.Out -match '(?m)^host\s+apphost \d+\.\d+\.\d+.*hostfxr \d+.*hostpolicy \d+' -and $r.Out -match '(?m)^mode\s+app \(' -and $r.Out -match '(?m)^app\s+self-contained, runtime .*coreclr\.dll' -and $r.Out -match '(?m)^result\s+the host started the runtime; the app ended after .* with exit 7\.') "$($r.Code) $($r.Out)"
                    Remove-Item "$app\ApHostApp.deps.json"
                    $r = AP -t verify deps "$app\ApHostApp.exe"
                    Check 'deps: the same app without its deps.json: the host trap is reported' ($r.Code -eq 1 -and $r.Out -match '(?m)^HOST TRAP\s+ApHostApp\.deps\.json is missing') $r.Out
                    $r = AP -t verify start --name trap --exe "$app\ApHostApp.exe"
                    Check 'what deps says is what the .NET host does: started from its own folder it ends with 0x80008081' ($r.Out -match "cwd=$([regex]::Escape($app))" -and $r.Out -match 'already ended with exit 0x80008081 \.NET host: InvalidArgFailure') $r.Out
                    $r = AP -t verify start --name notrap --cwd $work --exe "$app\ApHostApp.exe"
                    Check '... and started from another folder it runs (its own exit code 7)' ($r.Out -match 'already ended with exit 7\.' -and (AP -t verify logs notrap --all).Out -match 'ap-host-app ran') "$($r.Out)`n$((AP -t verify logs notrap --all).Out)"
                    $r = AP -t verify deps "$app\ApHostApp.exe" --run
                    Check 'deps --run names the mode the host took (split/FX), the exit code and what the host wrote' ($r.Code -eq 1 -and $r.Out -match '(?m)^mode\s+split/FX: NOT how an app runs' -and $r.Out -match '(?m)^result\s+the host gave up before it started the runtime: exit 0x80008081 \.NET host: InvalidArgFailure' -and $r.Out -match '(?m)^host trace: .*hosttrace-.*\.log \(\d+ lines') "$($r.Code) $($r.Out)"
                    $r = AP -t verify deps "$app\ApHostApp.exe" --run --cwd $work
                    Check 'deps --run --cwd: from another folder the same app starts' ($r.Code -eq 0 -and $r.Out -match '(?m)^mode\s+app \(' -and $r.Out -match 'with exit 7\.') "$($r.Code) $($r.Out)"
                }

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

                [void](AP -t verify exec 'Write-Output ("z" * 5000)')
                $h = (AP -t verify history --last 200).Out
                Check 'history lists what was run, with exit codes' ($h -match 'exit 7\s+exec Write-Output' -and $h -match 'exit 124') $h
                Check 'history: OUT is how much each command wrote (5000 characters and a line break: 5K), and a line adds it up' ($h -match '(?m)^\s*#\s+START\s+TOOK\s+OUT\s+RESULT\s+COMMAND' -and $h -match '(?m)\s5K\s+exit 0\s+exec Write-Output \("z" \* 5000\)' -and $h -match '(?m)^OUT: \S+ characters of output in these \d+ commands') (($h -split "`n" | Select-Object -Last 3) -join "`n")
                $biggest = ((AP -t verify history --last 200 --by-size).Out -split "`n")[1]
                Check 'history --by-size puts the command that wrote most first (the 7 MB read)' ($biggest -match '\s\d+(\.\d+)?M\s+exit 0\s+read ') $biggest

                # A crash that Windows logs is added to a command's answer without being asked for, and only once.
                $crashAt = Get-Date
                $crashOut = (AP -t verify exec 'powershell -NoProfile -Command "[Environment]::FailFast(''agentprobe-verify'')"; exit 0').Out
                $logged = $null; 1..20 | ForEach-Object { if (-not $logged) { Start-Sleep 1; $logged = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; Id = 1000; StartTime = $crashAt } -ErrorAction SilentlyContinue | Where-Object { $_.Message -match 'powershell\.exe' } | Select-Object -First 1 } }
                if (-not $logged) { Write-Host '  skipped: Windows did not log the crash of the test process here, so there is nothing to report.' -ForegroundColor Yellow }
                else {
                    $next = (AP -t verify exec 'Write-Output after-crash').Out; $again = (AP -t verify exec 'Write-Output once-more').Out
                    $said = [regex]::Matches("$crashOut`n$next`n$again", 'Windows logged this on the target since the last command: \S+ \S+ CRASH powershell\.exe').Count
                    Check 'a crash Windows logged is added to a command''s answer, once' ($said -eq 1 -and $again -notmatch 'Windows logged this') "said $said times`n$crashOut`n$next`n$again"
                }

                # What changed since the last look: the last look is remembered on the target, so this needs the session.
                $form2 = Start-Process powershell -ArgumentList '-NoProfile', '-STA', '-WindowStyle', 'Hidden', '-ExecutionPolicy', 'Bypass', '-File', "`"$formFile`"" -PassThru
                if ((AP -t verify wait --window 'AP Verify Window' --timeout 40s).Code -ne 0) { Check 'the test window for the --diff checks appeared' $false '' }
                else {
                    Start-Sleep 1
                    $first = AP -t verify ui 'AP Verify Window' --diff
                    [void](AP -t verify ui set '#inputBox' 'diff me' --window 'AP Verify Window' --no-watch)
                    $second = AP -t verify ui 'AP Verify Window' --diff
                    $third = AP -t verify ui 'AP Verify Window' --diff
                    Check 'ui --diff: the first look is the whole listing and says why; then only what changed; then nothing' ($first.Out -match 'nothing to compare with' -and $first.Out -match 'Button "Press me"' -and $second.Out -match '1 change since the last `ui` of it' -and $second.Out -match '(?m)^~ Edit.* #inputBox =diff me' -and $second.Out -notmatch 'Press me' -and $third.Out -match 'nothing changed among its \d+ listed controls') "$($first.Out)`n$($second.Out)`n$($third.Out)"
                    $d1 = Join-Path $work 'd1.png'; $d2 = Join-Path $work 'd2.png'; $d3 = Join-Path $work 'd3.png'
                    $whole = AP -t verify screenshot --window 'AP Verify Window' -o $d1
                    [void](AP -t verify ui set '#inputBox' 'other pixels' --window 'AP Verify Window' --no-watch); Start-Sleep -Milliseconds 400
                    $changed = AP -t verify screenshot --window 'AP Verify Window' --diff -o $d2
                    $wholeSize = if ($whole.Out -match '(\d+)x(\d+) PNG') { @([int]$Matches[1], [int]$Matches[2]) } else { @(0, 0) }
                    Check 'screenshot --diff: after a change, the picture is only the part that changed' ((Test-Path $d2) -and $changed.Out -match 'only the part that changed' -and $changed.Out -match '(\d+)x(\d+) PNG' -and [int]$Matches[1] -lt $wholeSize[0] -and [int]$Matches[2] -lt ($wholeSize[1] / 2)) "$($whole.Out)`n$($changed.Out)"
                    [void](AP -t verify ui click 'Show cover' --window 'AP Verify Window' --no-watch)
                    if ((AP -t verify wait --window 'AP Verify Cover' --timeout 10s).Code -eq 0) {
                        Start-Sleep 1
                        $before = AP -t verify screenshot --window 'AP Verify Cover' --diff -o $d3
                        Remove-Item $d3 -ErrorAction SilentlyContinue
                        $same = AP -t verify screenshot --window 'AP Verify Cover' --diff -o $d3
                        Check 'screenshot --diff: with no picture of that area before, it is the whole picture and says so; unchanged, it is a line of text and no image' ($before.Out -match 'no earlier screenshot of exactly this area' -and $same.Code -eq 0 -and $same.Out -match '^no change: ' -and $same.Out -match 'No image was sent' -and -not (Test-Path $d3)) "$($before.Out)`n$($same.Out)"
                    } else { Check 'the cover window for the screenshot --diff check appeared' $false '' }
                }
                Stop-Process -Id $form2.Id -Force -ErrorAction SilentlyContinue

                # MCP: several lines in one `run` are steps.
                $mp = [System.Diagnostics.Process]::Start($mcp)
                $call = @{ jsonrpc = '2.0'; id = 7; method = 'tools/call'; params = @{ name = 'run'; arguments = @{ target = 'verify'; command = "exec 'Write-Output step-one'`nshow exec 'Write-Output step-two'" } } } | ConvertTo-Json -Depth 6 -Compress
                $mp.StandardInput.WriteLine($call); $mp.StandardInput.Close()
                $answer = $mp.StandardOutput.ReadToEnd() -split "`n" | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object { $_.id -eq 7 }
                $mp.WaitForExit()
                $stepsText = "$($answer.result.content[0].text)"
                Check 'mcp: `run` with several lines runs them as steps (the output of the `show` step and one line)' ($stepsText -match 'step-two' -and $stepsText -notmatch 'step-one' -and $stepsText -match 'ok: 2 steps in') $stepsText
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
                if ((AP -t local windows).Out -notmatch 'SYSTEM PROMPT') {
                    $r = AP -t verify firewall dismiss
                    Check 'firewall dismiss without a pid, with no prompt on the screen: says so, closes nothing' ($r.Code -eq 0 -and $r.Out -match 'no prompt of the firewall''s host program .* is on the screen; nothing to close') "$($r.Code) $($r.Out)"
                    Check 'firewall dismiss with a pid that is not a prompt says whose pid it wants' ((AP -t verify firewall dismiss $PID).Out -match 'No prompt of the firewall''s host program is on the screen') ''
                    $r1 = AP -t verify firewall dismiss --auto; $r2 = AP -t verify firewall dismiss --auto; $r3 = AP -t verify firewall dismiss --auto off
                    Check 'firewall dismiss --auto: on for the session, says so when asked again, and off' ($r1.Code -eq 0 -and $r1.Out -match 'from now until the session ends' -and $r2.Out -match '^already on: 0 prompts closed' -and $r3.Out -match 'no longer closed automatically') "$($r1.Out) / $($r2.Out) / $($r3.Out)"
                }

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
                $h = (AP -t updtgt history --last 30).Out
                Check 'target update: history still lists the commands from before the update' ($h -match "(?m)exit 0 \(before the update\)\s+exec 'kept' \| Set-Content kept\.txt" -and $h -match '(?m)\supdated\s+selfupdate ') $h
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
