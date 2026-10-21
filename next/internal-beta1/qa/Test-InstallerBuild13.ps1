param([ValidateSet('Full','Client','Workflow')][string]$Role='Full')
$ErrorActionPreference='Stop'
$base=Split-Path $PSScriptRoot -Parent
$repo=Split-Path (Split-Path $base -Parent) -Parent
$candidate=Join-Path $base $(if($Role-ne'Client'){'.local/beta4-admin-build13-final'}else{'.local/beta4-client-build13-final'})
$manifest=Get-Content -LiteralPath (Join-Path $candidate 'artifacts/candidate-manifest.json') -Raw|ConvertFrom-Json
$msi=Join-Path $candidate ('artifacts/'+$manifest.artifact.name)
if($manifest.buildRevision-ne13-or(Get-FileHash -LiteralPath $msi).Hash-ne$manifest.artifact.sha256){throw 'Wrong candidate'}
$stamp=Get-Date -Format yyyyMMdd-HHmmss
$root='C:\TEST\KCAASetup-'+$stamp+'-'+$Role
$evidence=Join-Path $base ('.local/beta4-admin-build13-final/installer-qa-'+$stamp+'-'+$Role)
New-Item -ItemType Directory -Path $evidence|Out-Null
$credential=Import-Clixml -LiteralPath (Join-Path $repo 'next/local-prototype/.local/native-remote/testpc-credential.xml')
$session=New-PSSession -ComputerName 192.168.1.203 -Authentication Negotiate -Credential $credential
try {
 Invoke-Command -Session $session -ArgumentList $root -ScriptBlock {param($root)
  if($env:COMPUTERNAME-ne'DESKTOP-GMEG262'-or(Test-Path -LiteralPath $root)){throw 'Wrong test PC or existing root'}
  New-Item -ItemType Directory -Path $root,(Join-Path $root 'qa'),(Join-Path $root 'legacy'),(Join-Path $root 'share')|Out-Null
  Copy-Item -LiteralPath 'C:\Program Files\SMB Manager Internal Beta 1\runtime' -Destination $root -Recurse
 }
 Copy-Item -LiteralPath $msi -Destination (Join-Path $root 'candidate.msi') -ToSession $session
 Copy-Item -Path (Join-Path $base '.local/novice-build13/bin/SmokeQA/Release/net10.0/*') -Destination (Join-Path $root 'qa') -ToSession $session
 $version=if($Role-eq'Full'){'1.7.7'}else{'1.7.11'}
 foreach($name in @(('SMB Manager V'+$version+'.exe'),'Uninstall.exe','version.ini')){Copy-Item -LiteralPath (Join-Path $repo ('SMB Manager V'+$version+'/'+$name)) -Destination (Join-Path $root 'legacy') -ToSession $session}
 $runner={param([string]$Root,[string]$Share,[string]$Role)
  $ErrorActionPreference='Stop';$exitCode=1
  try{
   & (Join-Path $Root 'runtime/dotnet.exe') (Join-Path $Root 'qa/SmokeQA.dll') $(if($Role-eq'Full'){'--admin-isolation-qa'}elseif($Role-eq'Workflow'){'--native-smb-qa'}else{'--installer-reset-qa'}) --root $Root --msi (Join-Path $Root 'candidate.msi') --test-share ('\\'+$env:COMPUTERNAME+'\'+$Share) --legacy-fixture (Join-Path $Root 'legacy') --role $Role *> (Join-Path $Root 'console.log')
   $exitCode=$LASTEXITCODE
  }catch{[IO.File]::WriteAllText((Join-Path $Root 'runner-error.txt'),$_.Exception.Message)}finally{@{exitCode=$exitCode}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $Root 'done.json') -Encoding UTF8}
 }
 $runnerFile=Join-Path $evidence 'run.ps1';[IO.File]::WriteAllText($runnerFile,$runner.ToString(),(New-Object Text.UTF8Encoding($true)))
 Copy-Item -LiteralPath $runnerFile -Destination (Join-Path $root 'run.ps1') -ToSession $session
 $result=Invoke-Command -Session $session -ArgumentList $root,$stamp,$Role,$manifest.artifact.sha256 -ScriptBlock {
  param($root,$stamp,$role,$hash)
  $ErrorActionPreference='Stop'
  if((Get-FileHash -LiteralPath (Join-Path $root 'candidate.msi')).Hash-ne$hash){throw 'MSI transfer mismatch'}
  $user='KCAASetup'+(Get-Date -Format HHmmss)+$role.Substring(0,1);$task='KCAASetup-'+$stamp+'-'+$role;$share=$(if($role-eq'Workflow'){ 'KCAAWorkflow' }else{ 'KCAASetup' })+$stamp.Replace('-','')+$role
  $made=$false;$taskMade=$false;$sid=$null;$complete=$false
  try {
   if(Get-LocalUser -Name $user -ErrorAction SilentlyContinue){throw 'Account name conflict'}
   $password='Aa1!'+[guid]::NewGuid().ToString('N')
   $account=New-LocalUser -Name $user -Password (ConvertTo-SecureString $password -AsPlainText -Force) -Description 'Disposable MSI and SMB reset QA';$sid=$account.SID.Value;$made=$true
   foreach($groupSid in @('S-1-5-32-544','S-1-5-32-545')){Add-LocalGroupMember -Group (Get-LocalGroup -SID $groupSid).Name -Member $user}
   $acl=Get-Acl -LiteralPath $root;$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($account.SID,'Modify','ContainerInherit,ObjectInherit','None','Allow')));Set-Acl -LiteralPath $root -AclObject $acl
   [IO.File]::WriteAllText((Join-Path $root 'share/qa-marker.txt'),$(if($role-eq'Workflow'){'KCAA WORKFLOW TEST'}else{'KCAA INSTALL RESET TEST'}))
   New-SmbShare -Name $share -Path (Join-Path $root 'share') -FullAccess ($env:COMPUTERNAME+'\'+$user)|Out-Null
   $action=New-ScheduledTaskAction -Execute powershell.exe -Argument ('-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "'+$root+'\run.ps1" -Root "'+$root+'" -Share "'+$share+'" -Role "'+$role+'"')
   Register-ScheduledTask -TaskName $task -Action $action -User ($env:COMPUTERNAME+'\'+$user) -Password $password -RunLevel Highest -Settings (New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 6))|Out-Null;$taskMade=$true
   $password=$null;Start-ScheduledTask -TaskName $task
   $deadline=(Get-Date).AddMinutes(4)
   while(!(Test-Path -LiteralPath (Join-Path $root 'done.json'))){if((Get-Date)-gt$deadline){throw 'Temporary account test timed out'};Start-Sleep -Seconds 1}
   $complete=$true
   Get-Content -LiteralPath (Join-Path $root 'done.json') -Raw
  }finally {
   $password=$null
   if($taskMade-and$complete){Unregister-ScheduledTask -TaskName $task -Confirm:$false}
   $s=Get-SmbShare -Name $share -ErrorAction SilentlyContinue;if($s-and$s.Path-eq(Join-Path $root 'share')){Remove-SmbShare -Name $share -Force}
   if($made-and$complete){
    foreach($p in @(Get-CimInstance Win32_Process -Filter "Name='msiexec.exe' OR Name='conhost.exe'")){$owner=Invoke-CimMethod -InputObject $p -MethodName GetOwnerSid -ErrorAction SilentlyContinue;if($owner.Sid-eq$sid){Stop-Process -Id $p.ProcessId -ErrorAction Stop}}
    $profile=Get-CimInstance Win32_UserProfile|Where-Object SID -eq $sid
    $unloadDeadline=(Get-Date).AddSeconds(20);while($profile-and$profile.Loaded-and(Get-Date)-lt$unloadDeadline){Start-Sleep -Seconds 1;$profile=Get-CimInstance Win32_UserProfile|Where-Object SID -eq $sid}
    if($profile){$resolved=[IO.Path]::GetFullPath($profile.LocalPath);if(!( $resolved.StartsWith('C:\Users\KCAASetup',[StringComparison]::OrdinalIgnoreCase))-or(Split-Path $resolved -Leaf)-ne$user){throw 'Disposable profile cleanup guard failed'};if(!$profile.Loaded){Remove-CimInstance -InputObject $profile}}
    Remove-LocalUser -Name $user
   }
  }
 }
 foreach($pattern in @('*.json','*.log','runner-error.txt')){Copy-Item -Path (Join-Path $root $pattern) -Destination $evidence -FromSession $session -ErrorAction SilentlyContinue}
 $result|Set-Content -LiteralPath (Join-Path $evidence 'runner-done.json') -Encoding UTF8
 Get-Content -LiteralPath (Join-Path $evidence $(if($Role-eq'Workflow'){'native-smb-report.json'}else{'installer-reset-'+$Role+'.json'})) -Raw
 if(($result|ConvertFrom-Json).exitCode-ne0){throw 'MSI completion native QA failed'}
}finally{Remove-PSSession $session;Write-Output ('EVIDENCE '+$evidence)}