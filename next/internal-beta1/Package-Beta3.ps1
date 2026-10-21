param([switch]$SkipBuild,[string]$CandidateRoot=(Join-Path $PSScriptRoot '.local\beta1-build11-install'))
$ErrorActionPreference='Stop'
[xml]$buildProps=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Directory.Build.props') -Raw
if($buildProps.Project.PropertyGroup.FileVersion-ne'2.0.119.0'){throw 'Packaging script version does not match current source. Use the matching build script.'}
$version='2.0.0-beta.3'
$work=[IO.Path]::GetFullPath($CandidateRoot)
$payload=Join-Path $work 'payload'
$out=Join-Path $work 'artifacts'
$wix=Join-Path $PSScriptRoot '..\local-prototype\.local\tools\wix.exe'
if(!(Test-Path -LiteralPath $wix)){throw 'Install WiX 6.0.2 in .local/tools before packaging.'}
if(Test-Path -LiteralPath $payload){throw 'Payload already exists. Use a fresh candidate directory; do not mix files from previous builds.'}
if(!$SkipBuild){& (Join-Path $PSScriptRoot 'Build.ps1') -OutputRoot $work}
New-Item -ItemType Directory -Force $payload,$out|Out-Null
foreach($project in @('ManagementServer','AdminDesktop','ClientDesktop','AlphaLauncher')){
 $target=Join-Path $payload $project
 & dotnet publish (Join-Path $PSScriptRoot ($project+'\'+$project+'.csproj')) -c Release -r win-x64 --self-contained false --ignore-failed-sources -p:NuGetAudit=false "-p:SmbOutputRoot=$work" -p:AppHostDotNetSearch=AppRelative -p:AppHostRelativeDotNet=../runtime -o $target
 if($LASTEXITCODE -ne 0){throw ('Publish failed: '+$project)}
 Get-ChildItem -LiteralPath $target -Filter '*.pdb'|Remove-Item
}
# Ship the installed official runtime as ordinary files; no extraction or downloads at application startup.
$runtimeVersion='10.0.12'
$runtime=Join-Path $payload 'runtime'
$installed=Join-Path $env:ProgramFiles 'dotnet'
New-Item -ItemType Directory -Force $runtime|Out-Null
foreach($file in @('dotnet.exe','LICENSE.txt','ThirdPartyNotices.txt')){Copy-Item -LiteralPath (Join-Path $installed $file) -Destination $runtime}
foreach($part in @('host\fxr','shared\Microsoft.NETCore.App','shared\Microsoft.WindowsDesktop.App','shared\Microsoft.AspNetCore.App')){
 $relative=$part+'\'+$runtimeVersion;$target=Join-Path $runtime $relative
 New-Item -ItemType Directory -Force $target|Out-Null
 Copy-Item -Path (Join-Path (Join-Path $installed $relative) '*') -Destination $target -Recurse
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'INTERNAL-BETA.md') -Destination $payload
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'BETA-CHANNELS.md') -Destination $payload
$sb=New-Object Text.StringBuilder
function Xml([string]$value){[Security.SecurityElement]::Escape($value)}
[void]$sb.AppendLine('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs" xmlns:ui="http://wixtoolset.org/schemas/v4/wxs/ui">')
[void]$sb.AppendLine('<Package Name="SMB Manager Setup Installer (2.0.0-beta.3)" Manufacturer="SMB Manager Project" Version="2.0.119" Language="1042" Codepage="949" UpgradeCode="0E6FD877-B208-4ECF-BA0F-19C312F69E7B" Scope="perMachine">')
[void]$sb.AppendLine('<SummaryInformation Description="SMB Manager 부서 내부 베타 1" /> <MajorUpgrade DowngradeErrorMessage="더 최신 내부 베타가 설치되어 있습니다." /> <MediaTemplate EmbedCab="yes" />')
[void]$sb.AppendLine('<Property Id="ARPCOMMENTS" Value="2.0.0-beta.3 / 부서 내부 시험 전용 / 미서명" /><Icon Id="AppIcon" SourceFile="'+(Xml (Join-Path $PSScriptRoot 'Assets\app-icon.ico'))+'"/><Property Id="ARPPRODUCTICON" Value="AppIcon"/>')
[void]$sb.AppendLine('<StandardDirectory Id="ProgramFiles64Folder"><Directory Id="INSTALLFOLDER" Name="SMB Manager Internal Beta 1">')
$dirs=@{}
$files=@(Get-ChildItem -LiteralPath $payload -Recurse -File|Sort-Object FullName)
$index=0
foreach($file in $files){
 $relative=$file.FullName.Substring($payload.Length+1);$parts=$relative.Split('\');$opened=0
 for($i=0;$i -lt $parts.Length-1;$i++){
  $key=($parts[0..$i] -join '\');if(!$dirs.ContainsKey($key)){$dirs[$key]='D'+$dirs.Count}
  [void]$sb.AppendLine('<Directory Id="'+$dirs[$key]+'" Name="'+(Xml $parts[$i])+'">');$opened++
 }
 $id='F'+$index;$component='C'+$index
 [void]$sb.AppendLine('<Component Id="'+$component+'" Guid="*" Bitness="always64"><File Id="'+$id+'" Source="'+(Xml $file.FullName)+'" KeyPath="yes" /></Component>')
 if($relative -eq 'AlphaLauncher\AlphaLauncher.exe'){$launcherId=$id}
 if($relative -eq 'ClientDesktop\ClientDesktop.exe'){$clientId=$id}
 for($i=0;$i -lt $opened;$i++){[void]$sb.AppendLine('</Directory>')}
 $index++
}
[void]$sb.AppendLine('</Directory></StandardDirectory><StandardDirectory Id="ProgramMenuFolder"><Directory Id="MenuFolder" Name="SMB Manager Internal Beta 1"><Component Id="Shortcuts" Guid="*" Bitness="always64">')
[void]$sb.AppendLine('<Shortcut Id="AdminShortcut" Name="SMB Manager Internal Beta 1 관리자" Target="[#'+$launcherId+']" Icon="AppIcon"/><Shortcut Id="ClientShortcut" Name="SMB Manager Internal Beta 1 클라이언트" Target="[#'+$clientId+']" Icon="AppIcon"/><RemoveFolder Id="RemoveMenu" On="uninstall"/><RegistryValue Root="HKLM" Key="Software\SMB Manager\InternalBeta1" Name="Installed" Type="integer" Value="1" KeyPath="yes"/>')
[void]$sb.AppendLine('</Component></Directory></StandardDirectory><Feature Id="Complete" Title="SMB Manager Internal Beta 1" Level="1"><ComponentRef Id="Shortcuts"/>')
for($i=0;$i -lt $index;$i++){[void]$sb.AppendLine('<ComponentRef Id="C'+$i+'"/>')}
[void]$sb.AppendLine('</Feature><ui:WixUI Id="WixUI_Minimal"/><WixVariable Id="WixUILicenseRtf" Value="'+(Xml (Join-Path $PSScriptRoot 'Installer\Beta8Notice.rtf'))+'"/></Package></Wix>')
[void]$sb.Replace('</Package></Wix>','<CustomAction Id="CompleteUserMigration" FileRef="'+$clientId+'" ExeCommand="--complete-install --install-user-sid &quot;[UserSID]&quot; --install-ui-level [UILevel]" Execute="immediate" Return="check" Impersonate="yes" /><InstallExecuteSequence><Custom Action="CompleteUserMigration" After="InstallFinalize" Condition="NOT REMOVE~=&quot;ALL&quot; AND NOT UPGRADINGPRODUCTCODE" /></InstallExecuteSequence></Package></Wix>')
$source=Join-Path $work 'Package.wxs'
# Merge repeated directory declarations into one tree before compiling.
$doc=New-Object Xml.XmlDocument;$doc.LoadXml($sb.ToString())
function MergeDirectories($parent){
 $seen=@{};foreach($child in @($parent.ChildNodes)){if($child.LocalName -eq 'Directory'){
  $key=$child.GetAttribute('Id');if($seen.ContainsKey($key)){foreach($node in @($child.ChildNodes)){[void]$seen[$key].AppendChild($node)};[void]$parent.RemoveChild($child)}else{$seen[$key]=$child}
 }};foreach($child in @($parent.ChildNodes)){if($child.LocalName -in @('Directory','StandardDirectory','Package','Wix')){MergeDirectories $child}}
}
MergeDirectories $doc.DocumentElement;$doc.Save($source)
$msi=Join-Path $out ('SMB-Manager-'+$version+'-build11-x64.msi')
& $wix build $source -arch x64 -ext WixToolset.UI.wixext -culture ko-kr -out $msi -pdbtype none
if($LASTEXITCODE -ne 0){throw 'MSI build failed.'}
$manifest=@{version=$version;buildRevision=11;fileVersion='2.0.119.0';channels=@('closed-beta','open-beta');runtimeVersion=$runtimeVersion;publicApproved=$false;status='InternalOnlyUnsignedCandidate';distribution='PrivateDepartmentOnly';updateChannelPublished=$false;createdAt=(Get-Date).ToString('o');artifact=@{name=[IO.Path]::GetFileName($msi);sha256=(Get-FileHash $msi -Algorithm SHA256).Hash};files=@($files|ForEach-Object{@{path=$_.FullName.Substring($payload.Length+1);sha256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash}})}
$manifest|ConvertTo-Json -Depth 5|Set-Content (Join-Path $out 'candidate-manifest.json') -Encoding utf8
Write-Output ('CANDIDATE '+$msi)
