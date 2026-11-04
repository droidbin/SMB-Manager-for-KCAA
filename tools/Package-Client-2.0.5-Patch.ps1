param(
    [string]$SourceCandidate = "C:\Users\Admin\Documents\Codex_Project\SMB Manager\next\internal-beta1\.local\beta5-admin-build21",
    [string]$CandidateRoot = "C:\Users\Admin\Documents\Gemini_Project\SMB-Manager-for-KCAA\.local\client-2.0.5-patch-work",
    [string]$DistDir = "C:\Users\Admin\Documents\Gemini_Project\SMB-Manager-for-KCAA\dist"
)
$ErrorActionPreference = 'Stop'

$parent = [IO.Path]::GetFullPath($SourceCandidate)
$work = [IO.Path]::GetFullPath($CandidateRoot)
$codexInternalBeta1 = "C:\Users\Admin\Documents\Codex_Project\SMB Manager\next\internal-beta1"

if (Test-Path -LiteralPath $work) {
    Remove-Item -LiteralPath $work -Recurse -Force
}

$sourcePayload = Join-Path $parent 'payload'
$parentManifest = Get-Content -LiteralPath (Join-Path $parent 'artifacts/candidate-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($parentManifest.buildRevision -ne 21 -or $parentManifest.artifact.sha256 -ne (Get-FileHash -LiteralPath (Join-Path $parent 'artifacts/SMB-Manager-2.0.0-beta.5-build21-x64.msi')).Hash) {
    throw 'Unexpected immutable build21 source candidate.'
}

$payload = Join-Path $work 'payload'
$out = Join-Path $work 'artifacts'
New-Item -ItemType Directory -Path $payload, $out -Force | Out-Null

$clientPayload = Join-Path $payload 'ClientDesktop'
$clientProject = "C:\Users\Admin\Documents\Gemini_Project\SMB-Manager-for-KCAA\next\client-2.0\ClientDesktop\ClientDesktop.csproj"

Write-Host "Publishing ClientDesktop..."
& dotnet publish $clientProject -c Release -r win-x64 --self-contained false --ignore-failed-sources -p:NuGetAudit=false "-p:SmbOutputRoot=$work" -p:FileVersion=2.0.146.0 -p:InformationalVersion=2.0.5+qa.38 -p:AppHostDotNetSearch=AppRelative -p:AppHostRelativeDotNet=../runtime -o $clientPayload
if ($LASTEXITCODE -ne 0) { throw 'Client publish failed' }

Get-ChildItem -LiteralPath $clientPayload -Filter '*.pdb' | Remove-Item

$runtime = Join-Path $payload 'runtime'
New-Item -ItemType Directory -Path $runtime | Out-Null
foreach ($name in @('dotnet.exe', 'LICENSE.txt', 'ThirdPartyNotices.txt')) {
    Copy-Item -LiteralPath (Join-Path $sourcePayload ('runtime/' + $name)) -Destination $runtime
}
foreach ($part in @('host', 'shared/Microsoft.NETCore.App', 'shared/Microsoft.WindowsDesktop.App')) {
    $destination = Join-Path $runtime $part
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Copy-Item -Path (Join-Path (Join-Path $sourcePayload ('runtime/' + $part)) '*') -Destination $destination -Recurse
}
Copy-Item -LiteralPath (Join-Path $sourcePayload 'InstallerSupport') -Destination $payload -Recurse

# Sign assemblies
Write-Host "Signing company files..."
$owned = @(Get-ChildItem -LiteralPath $payload -Recurse -File | Where-Object {
    $_.Extension -in @('.exe', '.dll') -and $_.BaseName -in @('ManagementServer', 'AdminDesktop', 'AlphaLauncher', 'ClientDesktop', 'ClientEngine', 'Contracts') -and !$_.FullName.StartsWith((Join-Path $payload 'runtime') + '\', [StringComparison]::OrdinalIgnoreCase)
} | ForEach-Object FullName)

& (Join-Path $codexInternalBeta1 'Installer/Sign-CompanyFiles.ps1') -Files $owned

$files = @(Get-ChildItem -LiteralPath $payload -Recurse -File | Sort-Object FullName)

function EscapeRtf([string]$value) {
    $builder = New-Object Text.StringBuilder
    foreach ($c in $value.ToCharArray()) {
        if ([int]$c -gt 127) {
            [void]$builder.Append('\u' + ([int]$c - 65536 * [int]([int]$c -gt 32767)) + '?')
        } elseif ($c -in @('\', '{', '}')) {
            [void]$builder.Append('\' + $c)
        } elseif ($c -eq "`n") {
            [void]$builder.Append('\par ')
        } elseif ($c -ne "`r") {
            [void]$builder.Append($c)
        }
    }
    return $builder.ToString()
}

$notice = 'SMB Manager 2.0.5 정식 클라이언트 전용 설치본입니다. 관리자 및 서버 프로그램은 포함하지 않습니다. 설치 마지막 단계에서 확인된 구버전 설치 파일과 이전 바로가기를 자동 정리하고, 설치한 Windows 사용자의 기존 SMB 연결과 저장 매핑을 해제합니다. 공유폴더의 작업 파일과 탐색기 창을 먼저 닫고, 실행 중인 기존 직원 앱은 설치 과정에서 자동 종료됩니다. 열린 파일을 강제로 끊지 않으며 해제가 끝나지 않으면 설치 마무리 실패로 알립니다. 기존 설정·저장 비밀번호·공유폴더 연결과 관련 없는 파일은 보존합니다. 파일 잠금 등으로 실패하면 정리를 완료로 처리하지 않고 재시도를 안내합니다. 장치 등록 후 관리자 부서 승인이 필요합니다. KCAA 사내 코드 서명 인증서로 서명되었으며 설치 중 공개 인증서를 이 PC의 신뢰 저장소에 자동 등록합니다. 설치 완료 화면에서 마침을 누르면 앱이 일반 사용자 권한으로 자동 실행됩니다.'
$license = Join-Path $work 'ClientNotice.rtf'
[IO.File]::WriteAllText($license, '{\rtf1\ansi\ansicpg949\deff0{\fonttbl{\f0 Malgun Gothic;}}\f0\fs20 ' + (EscapeRtf $notice) + '}', (New-Object Text.ASCIIEncoding))

$doc = New-Object Xml.XmlDocument
$wixHeader = '<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs" xmlns:ui="http://wixtoolset.org/schemas/v4/wxs/ui">'
$wixPackage = '<Package Name="SMB Manager Client (2.0.5)" Manufacturer="SMB Manager Project" Version="2.0.146" Language="1042" Codepage="949" UpgradeCode="7D2BAF09-39A3-481C-81EE-1AFC7F79CA10" Scope="perMachine">'
$wixSummary = '<SummaryInformation Description="SMB Manager 2.0.5 Client"/>'
$wixUpgrade = '<MajorUpgrade Schedule="afterInstallInitialize" DowngradeErrorMessage="A newer version of SMB Manager Client is already installed."/>'
$wixMedia = '<MediaTemplate EmbedCab="yes"/>'
$wixProp = '<Property Id="ARPCOMMENTS" Value="SMB Manager 2.0.5"/>'
$wixDir = '<StandardDirectory Id="ProgramFiles64Folder"><Directory Id="INSTALLFOLDER" Name="SMB Manager Client Internal Beta 1"/></StandardDirectory>'
$wixFeat = '<Feature Id="Complete" Title="SMB Manager Client" Level="1"/>'
$wixUI = '<ui:WixUI Id="WixUI_Minimal"/>'
$wixXml = $wixHeader + $wixPackage + $wixSummary + $wixUpgrade + $wixMedia + $wixProp + $wixDir + $wixFeat + $wixUI + '</Package></Wix>'

$doc.LoadXml($wixXml)

$ns = 'http://wixtoolset.org/schemas/v4/wxs'
function Element([string]$name, [hashtable]$attributes) {
    $e = $doc.CreateElement($name, $ns)
    foreach ($key in $attributes.Keys) { $e.SetAttribute($key, [string]$attributes[$key]) }
    return ,$e
}

$package = $doc.DocumentElement.FirstChild
$install = $package.SelectSingleNode("*[local-name()='StandardDirectory']/*")
$feature = $package.SelectSingleNode("*[local-name()='Feature']")
$directories = @{'' = $install}; $index = 0; $clientId = $null

foreach ($file in $files) {
    $relative = $file.FullName.Substring($payload.Length + 1)
    $parts = $relative.Split('\')
    $current = $install; $key = ''
    for ($i = 0; $i -lt $parts.Length - 1; $i++) {
        $key = ($parts[0..$i] -join '\')
        if (!$directories.ContainsKey($key)) {
            $dir = Element 'Directory' @{Id = ('D' + $directories.Count); Name = $parts[$i]}
            [void]$current.AppendChild($dir)
            $directories[$key] = $dir
        }
        $current = $directories[$key]
    }
    $id = 'F' + $index; $cid = 'C' + $index
    $component = Element 'Component' @{Id = $cid; Guid = '*'; Bitness = 'always64'}
    [void]$component.AppendChild((Element 'File' @{Id = $id; Source = $file.FullName; KeyPath = 'yes'}))
    [void]$current.AppendChild($component)
    [void]$feature.AppendChild((Element 'ComponentRef' @{Id = $cid}))
    if ($relative -eq 'ClientDesktop\ClientDesktop.exe') { $clientId = $id }
    $index++
}

if (!$clientId) { throw 'Client executable missing.' }

# Start menu shortcut: 'SMB Manager' (Cleaned from Beta)
$programMenu = Element 'StandardDirectory' @{Id = 'ProgramMenuFolder'}
$menu = Element 'Directory' @{Id = 'ClientMenuFolder'; Name = 'SMB Manager'}
$shortcut = Element 'Component' @{Id = 'ClientShortcuts'; Guid = '*'; Bitness = 'always64'}
[void]$shortcut.AppendChild((Element 'Shortcut' @{Id = 'ClientShortcut'; Name = 'SMB Manager'; Target = ('[#' + $clientId + ']'); Icon = 'ClientIcon'}))
[void]$shortcut.AppendChild((Element 'RemoveFolder' @{Id = 'RemoveClientMenu'; On = 'uninstall'}))
[void]$shortcut.AppendChild((Element 'RegistryValue' @{Root='HKLM'; Key='Software\SMB Manager\ClientInternalBeta1'; Name='Installed'; Type='integer'; Value='1'; KeyPath='yes'}))
[void]$menu.AppendChild($shortcut)
[void]$programMenu.AppendChild($menu)
[void]$package.AppendChild($programMenu)

# Desktop shortcut: 'SMB Manager' (Cleaned from '알파테스트/클로즈베타 클라이언트')
$desktop = Element 'StandardDirectory' @{Id = 'DesktopFolder'}
$desktopComponent = Element 'Component' @{Id = 'ClientDesktopShortcut'; Guid = '*'; Bitness = 'always64'}
[void]$desktopComponent.AppendChild((Element 'Shortcut' @{Id = 'ClientDesktopLink'; Name = 'SMB Manager'; Target = ('[#' + $clientId + ']'); Icon = 'ClientIcon'}))
[void]$desktopComponent.AppendChild((Element 'RegistryValue' @{Root='HKLM'; Key='Software\SMB Manager\ClientInternalBeta1'; Name='DesktopShortcut'; Type='integer'; Value='1'; KeyPath='yes'}))
[void]$desktop.AppendChild($desktopComponent)
[void]$package.AppendChild($desktop)

foreach ($id in @('ClientShortcuts', 'ClientDesktopShortcut')) {
    [void]$feature.AppendChild((Element 'ComponentRef' @{Id = $id}))
}

[void]$package.AppendChild((Element 'Icon' @{Id = 'ClientIcon'; SourceFile = "C:\Users\Admin\Documents\Gemini_Project\SMB-Manager-for-KCAA\next\client-2.0\Assets\app-icon.ico"}))
[void]$package.AppendChild((Element 'Property' @{Id = 'ARPPRODUCTICON'; Value = 'ClientIcon'}))
[void]$package.AppendChild((Element 'WixVariable' @{Id = 'WixUILicenseRtf'; Value = $license}))

$ca = Element 'CustomAction' @{Id = 'CompleteUserMigration'; FileRef = $clientId; ExeCommand = '--complete-install --install-user-sid "[UserSID]" --install-ui-level [UILevel]'; Execute = 'immediate'; Return = 'check'; Impersonate = 'yes'}
[void]$package.AppendChild($ca)

$sequence = Element 'InstallExecuteSequence' @{}
[void]$sequence.AppendChild((Element 'Custom' @{Action = 'CompleteUserMigration'; After = 'InstallFinalize'; Condition = 'NOT REMOVE~="ALL" AND NOT UPGRADINGPRODUCTCODE'}))
[void]$package.AppendChild($sequence)

. (Join-Path $codexInternalBeta1 'Installer/Add-ClientWindowsCompatibility.ps1')
Add-ClientWindowsCompatibility -Document $doc
. (Join-Path $codexInternalBeta1 'Installer/Add-InstalledAppShutdown.ps1')
Add-InstalledAppShutdown -Document $doc -Role client -WorkRoot $work
. (Join-Path $codexInternalBeta1 'Installer/Add-CompanyCertificateTrust.ps1')
Add-CompanyCertificateTrust -Document $doc -WorkRoot $work
. (Join-Path $codexInternalBeta1 'Installer/Add-PostInstallLaunch.ps1')
Add-PostInstallLaunch -Document $doc -FileId $clientId -Role client
. (Join-Path $codexInternalBeta1 'Installer/Add-ClientLogonStartup.ps1')
Add-ClientLogonStartup -Document $doc -FileId $clientId
. (Join-Path $codexInternalBeta1 'Installer/Add-WorkPortalShortcut.ps1')
Add-WorkPortalShortcut -Document $doc -Role client

$wxs = Join-Path $work 'Client.wxs'
$doc.Save($wxs)

$msiName = "SMB-Manager-Client-2.0.5-Patch-x64.msi"
$msi = Join-Path $out $msiName

Write-Host "Building MSI with WiX..."
$wixExe = "C:\Users\Admin\Documents\Codex_Project\SMB Manager\next\local-prototype\.local\tools\wix.exe"
$uiExt = "C:\Users\Admin\Documents\Codex_Project\SMB Manager\.wix\extensions\WixToolset.UI.wixext\6.0.2\wixext6\WixToolset.UI.wixext.dll"
$utilExt = "C:\Users\Admin\Documents\Codex_Project\SMB Manager\.wix\extensions\WixToolset.Util.wixext\6.0.2\wixext6\WixToolset.Util.wixext.dll"

& $wixExe build $wxs -arch x64 -ext $uiExt -ext $utilExt -culture ko-kr -out $msi -pdbtype none
if ($LASTEXITCODE -ne 0) { throw 'Client MSI build failed.' }

Write-Host "Signing final MSI..."
& (Join-Path $codexInternalBeta1 'Installer/Sign-CompanyFiles.ps1') -Files @($msi)

New-Item -ItemType Directory -Path $DistDir -Force | Out-Null
$finalMsi = Join-Path $DistDir $msiName
Copy-Item -LiteralPath $msi -Destination $finalMsi -Force

$hash = (Get-FileHash -LiteralPath $finalMsi -Algorithm SHA256).Hash
Write-Host "==========================================" -ForegroundColor Green
Write-Host "MSI Package Built Successfully!" -ForegroundColor Green
Write-Host "File: $finalMsi" -ForegroundColor Green
Write-Host "SHA256: $hash" -ForegroundColor Green
Write-Host "==========================================" -ForegroundColor Green
