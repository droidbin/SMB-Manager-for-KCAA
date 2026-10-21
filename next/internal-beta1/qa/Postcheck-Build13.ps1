$ErrorActionPreference='Stop'
$base=Split-Path $PSScriptRoot -Parent;$repo=Split-Path (Split-Path $base -Parent) -Parent
$manager=Get-Content -LiteralPath (Join-Path $base '.local/beta4-admin-build13-final/artifacts/candidate-manifest.json') -Raw|ConvertFrom-Json
$client=Get-Content -LiteralPath (Join-Path $base '.local/beta4-client-build13-final/artifacts/candidate-manifest.json') -Raw|ConvertFrom-Json
$credential=Import-Clixml -LiteralPath (Join-Path $repo 'next/local-prototype/.local/native-remote/testpc-credential.xml');$s=New-PSSession -ComputerName 192.168.1.203 -Authentication Negotiate -Credential $credential
try {
 $result=Invoke-Command -Session $s -ArgumentList $manager.files,$client.files -ScriptBlock {
  param($fullFiles,$clientFiles)
  $ErrorActionPreference='Stop'
  if($env:COMPUTERNAME-ne'DESKTOP-GMEG262'){throw 'Wrong target'}
  $roots=@('C:\Program Files\SMB Manager Internal Beta 1','C:\Program Files\SMB Manager Client Internal Beta 1');$groups=@($fullFiles,$clientFiles)
  for($i=0;$i-lt2;$i++){foreach($f in $groups[$i]){$path=[IO.Path]::GetFullPath((Join-Path $roots[$i] $f.path));if(!$path.StartsWith($roots[$i]+'\',[StringComparison]::OrdinalIgnoreCase)-or!(Test-Path -LiteralPath $path)-or(Get-FileHash -LiteralPath $path).Hash-ne$f.sha256){throw ('Installed file mismatch '+$f.path)}}}
  $tasks=@(Get-ScheduledTask|Where-Object {$_.TaskName-like'KCAASetup-*'-or$_.TaskName-like'KCAARender13-*'-or$_.TaskName-like'KCAAWorkflow-*'-or$_.TaskName-like'KCAAAccount-*'})
  $shares=@(Get-CimInstance Win32_Share|Where-Object {$_.Name-like'KCAASetup*'-or$_.Name-like'KCAAWorkflow*'-or$_.Name-like'KCAAAccount*'})
  $users=@(Get-LocalUser|Where-Object {$_.Name-like'KCAASetup*'-or$_.Name-like'KCAAqa*'})
  if($tasks.Count-or$shares.Count-or$users.Count){throw 'Temporary test resource remains'}
  $profiles=@();$removed=@()
  foreach($p in @(Get-CimInstance Win32_UserProfile|Where-Object {$_.LocalPath-like'C:\Users\KCAASetup*'})){
   $path=[IO.Path]::GetFullPath($p.LocalPath);$leaf=Split-Path $path -Leaf
   if($path.StartsWith('C:\Users\',[StringComparison]::OrdinalIgnoreCase)-and$leaf-match'^KCAASetup\d{6}[FCW]$'-and!(Get-LocalUser|Where-Object {$_.SID.Value-eq$p.SID})){
    if(!$p.Loaded){Remove-CimInstance -InputObject $p;$removed+=$path}else{$profiles+=@{path=$path;sid=$p.SID;loaded=$true;reason='Windows keeps the deleted test profile loaded; no force unload'}}
   }
  }
  $mv=(Get-Item -LiteralPath (Join-Path $roots[0] 'ManagementServer/ManagementServer.exe')).VersionInfo.FileVersion
  $cv=(Get-Item -LiteralPath (Join-Path $roots[1] 'ClientDesktop/ClientDesktop.exe')).VersionInfo.FileVersion
  if($mv-ne'2.0.121.0'-or$cv-ne'2.0.121.0'){throw 'Wrong installed versions'}
  @{managerVersion=$mv;clientVersion=$cv;managerFilesVerified=$fullFiles.Count;clientFilesVerified=$clientFiles.Count;temporaryAccounts=$users.Count;temporaryShares=$shares.Count;temporaryTasks=$tasks.Count;removedProfiles=$removed;retainedWindowsLoadedTestProfiles=$profiles;at=(Get-Date).ToString('o')}
 }
 $result|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $base '.local/beta4-admin-build13-final/artifacts/native-postcheck.json') -Encoding UTF8
 $result|ConvertTo-Json -Depth 6
}finally{Remove-PSSession $s}