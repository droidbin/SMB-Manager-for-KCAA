param([ValidatePattern('^1\.\d+\.\d+$')][string]$ClientVersion = '1.7.11')
$ErrorActionPreference = 'Stop'
$workspaceRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$runRoot = Join-Path $workspaceRoot ('.build-logs\real-reconnect-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $runRoot | Out-Null
$settingsPath = Join-Path $env:APPDATA 'SMB Manager\settings.ini'
$passwordPath = Join-Path $env:APPDATA 'SMB Manager\saved-passwords.ini'
$logPath = Join-Path $env:APPDATA 'SMB Manager\result.log'
$processName = 'SMB Manager V' + $ClientVersion
$appPath = Join-Path $env:LOCALAPPDATA ('Programs\SMB Manager\' + $processName + '.exe')
$settingsBackup = Join-Path $runRoot 'settings-original.ini'
$originalBytes = [IO.File]::ReadAllBytes($settingsPath)
[IO.File]::WriteAllBytes($settingsBackup, $originalBytes)
$originalText = [IO.File]::ReadAllText($settingsPath)
$passwordHash = (Get-FileHash -LiteralPath $passwordPath).Hash
$originalMappings = @(Get-SmbMapping | Select-Object LocalPath,RemotePath,Status)
$originalMappings | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runRoot 'mappings-before.json') -Encoding utf8
$server = [regex]::Match($originalText, '(?m)^ServerHost=([^\r\n]+)').Groups[1].Value
$share = [regex]::Match($originalText, '(?m)^Share0=([^\r\n]+)').Groups[1].Value
$otherShare = [regex]::Match($originalText, '(?m)^Share1=([^\r\n]+)').Groups[1].Value
$expectedUnc = '\\' + $server + '\' + $share
$conflictUnc = '\\' + $server + '\' + $otherShare
$missingShare = 'SMB_QA_MISSING_' + [guid]::NewGuid().ToString('N').Substring(0,12)
$missingUnc = '\\' + $server + '\' + $missingShare
$testDrive = @('T:','S:','R:','Q:','P:') | Where-Object {
    $candidate = $_
    !($originalMappings | Where-Object LocalPath -eq $candidate) -and !(Get-PSDrive -Name $candidate.TrimEnd(':') -ErrorAction SilentlyContinue)
} | Select-Object -First 1
if (!$testDrive -or !$server -or !$share -or !$otherShare) { throw 'Unable to reserve an isolated test drive.' }
$checks = [Collections.Generic.List[object]]::new()
$testStarted = $false
$failure = $null
$cleanupFailure = $null
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class RealReconnectClientExit {
 public delegate bool Callback(IntPtr h, IntPtr p);
 [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern uint RegisterWindowMessage(string name);
 [DllImport("user32.dll")] public static extern bool EnumWindows(Callback callback, IntPtr p);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint id);
 [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
 [DllImport("mpr.dll", CharSet=CharSet.Unicode)] public static extern int WNetGetConnection(string local, StringBuilder remote, ref int length);
 [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern uint QueryDosDevice(string local, StringBuilder remote, int length);
 public static bool Unused(string drive) {
  var buffer=new StringBuilder(32768); int length=buffer.Capacity;
  if(WNetGetConnection(drive,buffer,ref length)!=2250) return false;
  return QueryDosDevice(drive,buffer,buffer.Capacity)==0 && Marshal.GetLastWin32Error()==2;
 }
 public static void Exit(uint id) { var msg=RegisterWindowMessage("SmbManager.Setup.RequestExit");
  if(msg==0) throw new InvalidOperationException("Exit message unavailable");
  EnumWindows((h,p)=>{uint owner; GetWindowThreadProcessId(h,out owner); if(owner==id) PostMessage(h,msg,IntPtr.Zero,IntPtr.Zero); return true;},IntPtr.Zero); }
}
'@
function Stop-Client {
    $clients = @(Get-Process -Name $processName -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $appPath })
    foreach ($client in $clients) { [RealReconnectClientExit]::Exit([uint32]$client.Id) }
    foreach ($client in $clients) { if (!$client.WaitForExit(15000)) { throw 'Client did not exit gracefully; refusing configuration changes.' } }
}
function Start-Client { Start-Process -FilePath $appPath | Out-Null }
function Configure-Test([string]$ShareName) {
    $text = [regex]::Replace($originalText, '(?m)^Drive0=[^\r\n]*', ('Drive0=' + $testDrive))
    $text = [regex]::Replace($text, '(?m)^Share0=[^\r\n]*', ('Share0=' + $ShareName))
    $text = [regex]::Replace($text, '(?m)^ConnectionMonitorIntervalSeconds=[^\r\n]*', 'ConnectionMonitorIntervalSeconds=15')
    $text = [regex]::Replace($text, '(?m)^AutomaticUpdate=[^\r\n]*', 'AutomaticUpdate=False')
    [IO.File]::WriteAllText($settingsPath, $text, [Text.UTF8Encoding]::new($true))
}
function Mapping { @(Get-SmbMapping | Where-Object LocalPath -eq $testDrive) }
function Check([bool]$Condition, [string]$Name) {
    $checks.Add([pscustomobject]@{name=$Name; passed=$Condition; at=(Get-Date).ToString('o')})
    if (!$Condition) { throw ('FAIL ' + $Name) }
    Write-Output ('PASS ' + $Name)
}
function Wait-Mapping([string]$Unc) {
    $deadline = (Get-Date).AddSeconds(65)
    do {
        $mapping = @(Mapping)
        if ($mapping.Count -eq 1 -and $mapping[0].RemotePath -eq $Unc -and $mapping[0].Status -eq 'OK') { return }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    throw ('Timed out waiting for actual client mapping: ' + $testDrive)
}
function Remove-TestMapping {
    $mapping = @(Mapping)
    if ($mapping.Count -eq 0) { return }
    if ($mapping.Count -ne 1 -or $mapping[0].RemotePath -notin @($expectedUnc,$conflictUnc,$missingUnc)) { throw 'Unexpected owner of test drive; refusing removal.' }
    Remove-SmbMapping -LocalPath $testDrive -UpdateProfile -Force
}
function Original-Mappings-Preserved {
    $now = @(Get-SmbMapping)
    foreach ($old in $originalMappings) {
        if (!($now | Where-Object { $_.LocalPath -eq $old.LocalPath -and $_.RemotePath -eq $old.RemotePath -and $_.Status -eq $old.Status })) { return $false }
    }
    return $true
}
function Log-Count([int]$Offset,[string]$Needle) {
    $lines = @(Get-Content -LiteralPath $logPath -Encoding utf8)
    return @($lines | Select-Object -Skip $Offset | Where-Object { $_.Contains('주기적 자동 재연결') -and $_.Contains($Needle) }).Count
}
try {
    Stop-Client
    $testStarted = $true
    Configure-Test $share
    Start-Client
    Write-Output ('Actual client test drive: ' + $testDrive + '; interval: 15 seconds')
    Wait-Mapping $expectedUnc
    Check $true 'Client created isolated real NAS mapping'
    foreach ($iteration in 1..2) {
        Remove-TestMapping
        Check (@(Mapping).Count -eq 0) ('Disconnect observed, cycle ' + $iteration)
        Wait-Mapping $expectedUnc
        Check $true ('Actual client automatically restored mapping, cycle ' + $iteration)
        Check (Original-Mappings-Preserved) ('Original mappings preserved, cycle ' + $iteration)
    }
    # Stop only the application; the original six mappings remain active.
    Stop-Client
    Remove-TestMapping
    New-SmbMapping -LocalPath $testDrive -RemotePath $conflictUnc -Persistent $false | Out-Null
    $offset = @(Get-Content -LiteralPath $logPath -Encoding utf8).Count
    Start-Client
    $deadline = (Get-Date).AddSeconds(75)
    do { Start-Sleep -Seconds 1 } while ((Log-Count $offset '기존 드라이브 보존') -lt 2 -and (Get-Date) -lt $deadline)
    Check ((Log-Count $offset '기존 드라이브 보존') -ge 2) 'Drive conflict retried on two actual timer cycles'
    Check (@(Mapping)[0].RemotePath -eq $conflictUnc) 'Conflicting mapping was not replaced'
    Remove-TestMapping
    Wait-Mapping $expectedUnc
    Check $true 'Client recovered after drive conflict removed without restart'
    Check (Original-Mappings-Preserved) 'Original mappings preserved after conflict test'
    Stop-Client
    Remove-TestMapping
    # A removed mapping can still return ERROR_CONNECTION_UNAVAIL (1201) from WNet.
    # Use another genuinely unused DOS/network device for the actual bad-share call.
    $failureDrive = @('S:','R:','Q:','P:','O:') | Where-Object {
        $candidate = $_
        !($originalMappings | Where-Object LocalPath -eq $candidate) -and [RealReconnectClientExit]::Unused($candidate)
    } | Select-Object -First 1
    if (!$failureDrive) { throw 'No genuinely unused drive available for actual NAS failure test.' }
    $testDrive = $failureDrive
    Configure-Test $missingShare
    $offset = @(Get-Content -LiteralPath $logPath -Encoding utf8).Count
    Start-Client
    $deadline = (Get-Date).AddSeconds(90)
    do { Start-Sleep -Seconds 1 } while ((Log-Count $offset ($missingShare + '(67)')) -lt 2 -and (Get-Date) -lt $deadline)
    Check ((Log-Count $offset ($missingShare + '(67)')) -ge 2) 'Actual NAS returned SMB error 67 on two timer cycles'
    Check (@(Mapping).Count -eq 0) 'Failed share did not create an invalid mapping'
    Check ((Get-FileHash -LiteralPath $passwordPath).Hash -eq $passwordHash) 'Saved encrypted password file unchanged'
    Check (Original-Mappings-Preserved) 'Original mappings preserved after actual SMB failures'
} catch { $failure = $_.Exception.ToString(); Write-Output ('FAIL ' + $_.Exception.Message) }
finally {
    if ($testStarted) {
        try {
            Stop-Client
            Remove-TestMapping
            [IO.File]::WriteAllBytes($settingsPath, $originalBytes)
            Start-Client
            Check ((Get-FileHash -LiteralPath $settingsPath).Hash -eq (Get-FileHash -LiteralPath $settingsBackup).Hash) 'Original settings restored byte for byte'
            Check (Original-Mappings-Preserved) 'All original mappings and states preserved at completion'
            Check ((Get-FileHash -LiteralPath $passwordPath).Hash -eq $passwordHash) 'Saved password file preserved at completion'
        } catch { $cleanupFailure = $_.Exception.ToString(); Write-Output ('CLEANUP FAILURE ' + $_.Exception.Message) }
    }
    @{checks=@($checks.ToArray()); failure=$failure; cleanupFailure=$cleanupFailure; testDrive=$testDrive; server=$server;
      clientVersion=$ClientVersion; finishedAt=(Get-Date).ToString('o'); scope='Installed actual client timer + actual NAS SMB; no server outage injected'} |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $runRoot 'report.json') -Encoding utf8
    Write-Output ('REPORT ' + (Join-Path $runRoot 'report.json'))
}
if ($failure -or $cleanupFailure) { throw 'Real reconnect QA did not fully pass. See report.' }
