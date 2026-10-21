$ErrorActionPreference='Stop'
$base=Split-Path $PSScriptRoot -Parent;$repo=Split-Path (Split-Path $base -Parent) -Parent
$stamp=Get-Date -Format yyyyMMdd-HHmmss;$root='C:\TEST\KCAARender13-'+$stamp;$task='KCAARender13-'+$stamp
$evidence=Join-Path $base ('.local/beta4-admin-build13-final/native-ui-'+$stamp);New-Item -ItemType Directory -Path $evidence|Out-Null
$credential=Import-Clixml -LiteralPath (Join-Path $repo 'next/local-prototype/.local/native-remote/testpc-credential.xml');$session=New-PSSession -ComputerName 192.168.1.203 -Authentication Negotiate -Credential $credential
try{
 $runner={param([string]$Root)
  $ErrorActionPreference='Stop'
  try{
   $before=@([IO.DriveInfo]::GetDrives()|ForEach-Object Name|Sort-Object)
   $exe='C:\Program Files\SMB Manager Client Internal Beta 1\ClientDesktop\ClientDesktop.exe'
   if((Get-Item -LiteralPath $exe).VersionInfo.FileVersion-ne'2.0.121.0'){throw 'Wrong installed version'}
   $bootstrap=Join-Path $Root 'bootstrap.json';[IO.File]::WriteAllText($bootstrap,(@{url='http://127.0.0.1:9/';code='';name='QA';department=''}|ConvertTo-Json),(New-Object Text.UTF8Encoding($false)))
   foreach($size in @('default','minimum','popup-default','popup-minimum')){
    $image=Join-Path $Root ($size+'.png');$args=@('--bootstrap',$bootstrap,'--state-dir',(Join-Path $Root ('state-'+$size)),'--snapshot',$image);if($size-like'*minimum'){$args+='--minimum-size'};if($size-like'popup-*'){$args+='--notification-popup-preview'}
    $process=Start-Process -FilePath $exe -ArgumentList $args -PassThru
    $null=$process.Handle
    if(!$process.WaitForExit(25000)){throw 'Installed UI timed out'}
    if($process.ExitCode-ne0){throw 'Installed UI failed'}
    $layout=Get-Content -LiteralPath ($image+'.layout.json') -Raw|ConvertFrom-Json;if(@($layout.problems).Count){throw ($layout.problems-join'; ')}
   }
   $adminExe='C:\Program Files\SMB Manager Internal Beta 1\AdminDesktop\AdminDesktop.exe'
   if((Get-Item -LiteralPath $adminExe).VersionInfo.FileVersion-ne'2.0.121.0'){throw 'Wrong administrator version'}
   foreach($size in @('default','minimum')){
    $image=Join-Path $Root ('admin-update-'+$size+'.png');$arguments=@('--github-update-preview','--snapshot',$image)
    if($size-eq'minimum'){$arguments+='--minimum-size'}
    $process=Start-Process -FilePath $adminExe -ArgumentList $arguments -PassThru
    $null=$process.Handle
    if(!$process.WaitForExit(25000)-or$process.ExitCode-ne0){throw 'Installed administrator updater failed'}
    $layout=Get-Content -LiteralPath ($image+'.layout.json') -Raw|ConvertFrom-Json;if(@($layout.problems).Count){throw 'Installed administrator updater clipping'}
   }
   $after=@([IO.DriveInfo]::GetDrives()|ForEach-Object Name|Sort-Object)
   if(Compare-Object $before $after){throw 'Existing user drives changed'}
   @{phase='Passed';uiChecks=6;before=$before;after=$after;at=(Get-Date).ToString('o')}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $Root 'done.json') -Encoding UTF8
  }catch{@{phase='Failed';error=$_.Exception.Message}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $Root 'done.json') -Encoding UTF8}
 }
 Invoke-Command -Session $session -ArgumentList $root,$runner.ToString(),$task -ScriptBlock {param($root,$runner,$task)
  if($env:COMPUTERNAME-ne'DESKTOP-GMEG262'-or(Test-Path -LiteralPath $root)){throw 'Wrong target'};New-Item -ItemType Directory -Path $root|Out-Null
  $file=Join-Path $root 'run.ps1';[IO.File]::WriteAllText($file,$runner,(New-Object Text.UTF8Encoding($true)))
  $action=New-ScheduledTaskAction -Execute powershell.exe -Argument ('-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "'+$file+'" -Root "'+$root+'"')
  Register-ScheduledTask -TaskName $task -Action $action -Principal (New-ScheduledTaskPrincipal -UserId ($env:COMPUTERNAME+'\User') -LogonType Interactive -RunLevel Limited)|Out-Null;Start-ScheduledTask -TaskName $task
 }
 $deadline=(Get-Date).AddSeconds(90)
 do{$done=Invoke-Command -Session $session -ArgumentList $root -ScriptBlock {param($root)if(Test-Path -LiteralPath (Join-Path $root 'done.json')){Get-Content -LiteralPath (Join-Path $root 'done.json') -Raw}};if($done){break};Start-Sleep -Seconds 2}while((Get-Date)-lt$deadline)
 foreach($pattern in @('*.json','*.png')){Copy-Item -Path (Join-Path $root $pattern) -Destination $evidence -FromSession $session}
 $done
 if(!$done-or($done|ConvertFrom-Json).phase-ne'Passed'){throw 'Native installed UI failed'}
}finally{Invoke-Command -Session $session -ArgumentList $task -ScriptBlock {param($task)$t=Get-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue;if($t-and$t.State-ne'Running'){Unregister-ScheduledTask -TaskName $task -Confirm:$false}};Remove-PSSession $session;Write-Output ('EVIDENCE '+$evidence)}