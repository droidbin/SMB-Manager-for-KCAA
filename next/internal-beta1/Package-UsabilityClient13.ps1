param([string]$SourceCandidate=(Join-Path $PSScriptRoot '.local/beta4-admin-build13-final'),[string]$CandidateRoot=(Join-Path $PSScriptRoot '.local/beta4-client-build13-final'))
$ErrorActionPreference='Stop'
$parent=[IO.Path]::GetFullPath($SourceCandidate)
$work=[IO.Path]::GetFullPath($CandidateRoot)
if(Test-Path -LiteralPath $work){throw 'Existing client candidate preserved.'}
$sourcePayload=Join-Path $parent 'payload'
$parentManifest=Get-Content -LiteralPath (Join-Path $parent 'artifacts/candidate-manifest.json') -Raw|ConvertFrom-Json
if($parentManifest.buildRevision -ne 13 -or $parentManifest.publicApproved -or $parentManifest.artifact.sha256 -ne (Get-FileHash -LiteralPath (Join-Path $parent 'artifacts/SMB-Manager-2.0.0-beta.4-build13-x64.msi')).Hash){throw 'Unexpected build13 source candidate.'}
if($parentManifest.fileVersion-ne'2.0.121.0'){throw 'Wrong parent file version'}
foreach($entry in $parentManifest.files){$path=[IO.Path]::GetFullPath((Join-Path $sourcePayload $entry.path));if(!$path.StartsWith($sourcePayload+'\',[StringComparison]::OrdinalIgnoreCase)-or(Get-FileHash -LiteralPath $path).Hash-ne$entry.sha256){throw 'Parent payload integrity check failed'}}
$payload=Join-Path $work 'payload';$out=Join-Path $work 'artifacts'
New-Item -ItemType Directory -Path $payload,$out -Force|Out-Null
$clientPayload=Join-Path $payload 'ClientDesktop'
& dotnet publish (Join-Path $PSScriptRoot 'ClientDesktop/ClientDesktop.csproj') -c Release -r win-x64 --self-contained false --ignore-failed-sources -p:NuGetAudit=false "-p:SmbOutputRoot=$work" -p:AppHostDotNetSearch=AppRelative -p:AppHostRelativeDotNet=../runtime -o $clientPayload
if($LASTEXITCODE-ne0){throw 'Client publish failed'}
Get-ChildItem -LiteralPath $clientPayload -Filter '*.pdb'|Remove-Item
if((Get-Item -LiteralPath (Join-Path $clientPayload 'ClientDesktop.exe')).VersionInfo.FileVersion-ne'2.0.121.0'){throw 'Client version mismatch'}
$runtime=Join-Path $payload 'runtime';New-Item -ItemType Directory -Path $runtime|Out-Null
foreach($name in @('dotnet.exe','LICENSE.txt','ThirdPartyNotices.txt')){Copy-Item -LiteralPath (Join-Path $sourcePayload ('runtime/'+$name)) -Destination $runtime}
foreach($part in @('host','shared/Microsoft.NETCore.App','shared/Microsoft.WindowsDesktop.App')){
 $destination=Join-Path $runtime $part
 New-Item -ItemType Directory -Path $destination -Force|Out-Null
 Copy-Item -Path (Join-Path (Join-Path $sourcePayload ('runtime/'+$part)) '*') -Destination $destination -Recurse
}
$files=@(Get-ChildItem -LiteralPath $payload -Recurse -File|Sort-Object FullName)
foreach($file in $files){$relative=$file.FullName.Substring($payload.Length+1);if($relative.StartsWith('ClientDesktop\')){continue};$expected=@($parentManifest.files|Where-Object path -eq $relative);if($expected.Count -ne 1 -or (Get-FileHash -LiteralPath $file.FullName).Hash -ne $expected[0].sha256){throw ('Source file mismatch: '+$relative)}}
if(@($files|Where-Object {$_.FullName -match 'AdminDesktop|ManagementServer|AlphaLauncher|AspNetCore'}).Count){throw 'Administrator/server payload must not be shipped to clients.'}
function EscapeRtf([string]$value){$builder=New-Object Text.StringBuilder;foreach($c in $value.ToCharArray()){if([int]$c -gt 127){[void]$builder.Append('\u'+[int]$c+'?')}elseif($c -in @('\','{','}')){[void]$builder.Append('\'+$c)}elseif($c -eq "`n"){[void]$builder.Append('\par ')}elseif($c -ne "`r"){[void]$builder.Append($c)}};return $builder.ToString()}
$notice='SMB Manager 클로즈베타 빌드 13 클라이언트 전용 설치본입니다. 관리자 및 서버 프로그램은 포함하지 않습니다. 설치 마지막 단계에서 확인된 1.7 설치 파일과 바로가기를 자동 제거하고, 설치한 Windows 사용자의 기존 SMB 연결과 저장 매핑을 모두 해제합니다. 공유폴더의 작업 파일과 탐색기 창을 먼저 닫고, 알림 영역의 직원 앱에서 앱 완전히 종료를 선택하세요. 열린 파일을 강제로 끊지 않으며 해제가 끝나지 않으면 설치 마무리 실패로 알립니다. 기존 설정·저장 비밀번호·공유폴더 연결과 관련 없는 파일은 보존합니다. 파일 잠금 등으로 실패하면 정리를 완료로 처리하지 않고 재시도를 안내합니다. 기존 1.7 설정은 자동 이관되지 않습니다. 장치 등록 후 관리자 부서 승인이 필요합니다. 코드 서명 인증서는 없습니다. 내부 시험 전용이며 공개 채널에 게시하지 않습니다.'
$license=Join-Path $work 'ClientNotice.rtf'
[IO.File]::WriteAllText($license,'{\rtf1\ansi\ansicpg949\deff0{\fonttbl{\f0 Malgun Gothic;}}\f0\fs20 '+(EscapeRtf $notice)+'}',(New-Object Text.ASCIIEncoding))
$doc=New-Object Xml.XmlDocument
$doc.LoadXml('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs" xmlns:ui="http://wixtoolset.org/schemas/v4/wxs/ui"><Package Name="SMB Manager Setup Installer (Client 2.0.0-beta.4)" Manufacturer="SMB Manager Project" Version="2.0.121" Language="1042" Codepage="949" UpgradeCode="7D2BAF09-39A3-481C-81EE-1AFC7F79CA10" Scope="perMachine"><SummaryInformation Description="SMB Manager 클로즈베타 빌드 13 클라이언트"/><MajorUpgrade DowngradeErrorMessage="더 최신 클라이언트가 설치되어 있습니다."/><MediaTemplate EmbedCab="yes"/><Property Id="ARPCOMMENTS" Value="KCAA LABS / 클로즈베타 빌드 13 / 클라이언트 전용 / 미서명"/><StandardDirectory Id="ProgramFiles64Folder"><Directory Id="INSTALLFOLDER" Name="SMB Manager Client Internal Beta 1"/></StandardDirectory><Feature Id="Complete" Title="SMB Manager Client" Level="1"/><ui:WixUI Id="WixUI_Minimal"/></Package></Wix>')
$ns='http://wixtoolset.org/schemas/v4/wxs'
function Element([string]$name,[hashtable]$attributes){$e=$doc.CreateElement($name,$ns);foreach($key in $attributes.Keys){$e.SetAttribute($key,[string]$attributes[$key])};return ,$e}
$package=$doc.DocumentElement.FirstChild
$install=$package.SelectSingleNode("*[local-name()='StandardDirectory']/*")
$feature=$package.SelectSingleNode("*[local-name()='Feature']")
$directories=@{''=$install};$index=0;$clientId=$null
foreach($file in $files){
 $relative=$file.FullName.Substring($payload.Length+1);$parts=$relative.Split('\');$current=$install;$key=''
 for($i=0;$i -lt $parts.Length-1;$i++){$key=($parts[0..$i] -join '\');if(!$directories.ContainsKey($key)){$dir=Element 'Directory' @{Id=('D'+$directories.Count);Name=$parts[$i]};[void]$current.AppendChild($dir);$directories[$key]=$dir};$current=$directories[$key]}
 $id='F'+$index;$cid='C'+$index;$component=Element 'Component' @{Id=$cid;Guid='*';Bitness='always64'}
 [void]$component.AppendChild((Element 'File' @{Id=$id;Source=$file.FullName;KeyPath='yes'}));[void]$current.AppendChild($component)
 [void]$feature.AppendChild((Element 'ComponentRef' @{Id=$cid}))
 if($relative -eq 'ClientDesktop\ClientDesktop.exe'){$clientId=$id};$index++
}
if(!$clientId){throw 'Client executable missing.'}
$programMenu=Element 'StandardDirectory' @{Id='ProgramMenuFolder'};$menu=Element 'Directory' @{Id='ClientMenuFolder';Name='SMB Manager Client Beta'}
$shortcut=Element 'Component' @{Id='ClientShortcuts';Guid='*';Bitness='always64'}
[void]$shortcut.AppendChild((Element 'Shortcut' @{Id='ClientShortcut';Name='SMB Manager 클로즈베타 클라이언트';Target=('[#'+$clientId+']');Icon='ClientIcon'}))
[void]$shortcut.AppendChild((Element 'RemoveFolder' @{Id='RemoveClientMenu';On='uninstall'}))
[void]$shortcut.AppendChild((Element 'RegistryValue' @{Root='HKLM';Key='Software\SMB Manager\ClientInternalBeta1';Name='Installed';Type='integer';Value='1';KeyPath='yes'}))
[void]$menu.AppendChild($shortcut);[void]$programMenu.AppendChild($menu);[void]$package.AppendChild($programMenu)
$desktop=Element 'StandardDirectory' @{Id='DesktopFolder'};$desktopComponent=Element 'Component' @{Id='ClientDesktopShortcut';Guid='*';Bitness='always64'}
[void]$desktopComponent.AppendChild((Element 'Shortcut' @{Id='ClientDesktopLink';Name='SMB Manager 클로즈베타 클라이언트';Target=('[#'+$clientId+']');Icon='ClientIcon'}))
[void]$desktopComponent.AppendChild((Element 'RegistryValue' @{Root='HKLM';Key='Software\SMB Manager\ClientInternalBeta1';Name='DesktopShortcut';Type='integer';Value='1';KeyPath='yes'}))
[void]$desktop.AppendChild($desktopComponent);[void]$package.AppendChild($desktop)
foreach($id in @('ClientShortcuts','ClientDesktopShortcut')){[void]$feature.AppendChild((Element 'ComponentRef' @{Id=$id}))}
[void]$package.AppendChild((Element 'Icon' @{Id='ClientIcon';SourceFile=(Join-Path $PSScriptRoot 'Assets/app-icon.ico')}))
[void]$package.AppendChild((Element 'Property' @{Id='ARPPRODUCTICON';Value='ClientIcon'}))
[void]$package.AppendChild((Element 'WixVariable' @{Id='WixUILicenseRtf';Value=$license}))
$ca=Element 'CustomAction' @{Id='CompleteUserMigration';FileRef=$clientId;ExeCommand='--complete-install --install-user-sid "[UserSID]" --install-ui-level [UILevel]';Execute='immediate';Return='check';Impersonate='yes'}
[void]$package.AppendChild($ca)
$sequence=Element 'InstallExecuteSequence' @{}
[void]$sequence.AppendChild((Element 'Custom' @{Action='CompleteUserMigration';After='InstallFinalize';Condition='NOT REMOVE~="ALL" AND NOT UPGRADINGPRODUCTCODE'}));[void]$package.AppendChild($sequence)
$wxs=Join-Path $work 'Client.wxs';$doc.Save($wxs)
$msi=Join-Path $out 'SMB-Manager-Client-2.0.0-beta.4-build13-x64.msi'
& (Join-Path $PSScriptRoot '../local-prototype/.local/tools/wix.exe') build $wxs -arch x64 -ext (Join-Path $PSScriptRoot '../../.wix/extensions/WixToolset.UI.wixext/6.0.2/wixext6/WixToolset.UI.wixext.dll') -culture ko-kr -out $msi -pdbtype none
if($LASTEXITCODE -ne 0){throw 'Client MSI build failed.'}
@{version='2.0.0-beta.4';buildRevision=13;fileVersion='2.0.121.0';msiVersion='2.0.121';clientOnly=$true;sourceMsiSha256=$parentManifest.artifact.sha256;publicApproved=$false;updateChannelPublished=$false;legacyRemoval='InstallFinalizeCurrentUserLegacyAndAllSmbReset';artifact=@{name=[IO.Path]::GetFileName($msi);sha256=(Get-FileHash -LiteralPath $msi).Hash};files=@($files|ForEach-Object {@{path=$_.FullName.Substring($payload.Length+1);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}})}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $out 'candidate-manifest.json') -Encoding UTF8
Write-Output ('CLIENT MSI '+$msi)