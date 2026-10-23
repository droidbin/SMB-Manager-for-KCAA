$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$CurrentVersionDir = Split-Path -Parent $Root
$VersionFile = Join-Path $CurrentVersionDir "version.ini"
$Version = "V1.7.0"
if (Test-Path -LiteralPath $VersionFile) {
    foreach ($Line in Get-Content -LiteralPath $VersionFile -Encoding UTF8) {
        if ($Line -match '^\s*Version\s*=\s*(.+?)\s*$') {
            $Version = $Matches[1]
        }
    }
}
$PackageName = "SMB Manager $Version"
$AppName = "SMB Manager $Version"
$RepositoryRoot = [IO.Path]::GetFullPath((Join-Path $Root "..\..\..\.."))
$DistRoot = Join-Path $RepositoryRoot ".local\legacy-builds"
New-Item -ItemType Directory -Force -Path $DistRoot | Out-Null
$BuildLogDir = Join-Path $DistRoot ".build-logs"
New-Item -ItemType Directory -Force -Path $BuildLogDir | Out-Null
Set-ItemProperty -LiteralPath $BuildLogDir -Name Attributes -Value ([System.IO.FileAttributes]::Hidden)
$BuildLogPath = Join-Path $BuildLogDir ("$Version-build-" + (Get-Date -Format "yyyyMMdd-HHmmss") + ".log")
Start-Transcript -Path $BuildLogPath -Force | Out-Null

$Compiler = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

if (-not (Test-Path -LiteralPath $Compiler)) {
    $Compiler = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
}

if (-not (Test-Path -LiteralPath $Compiler)) {
    throw "C# compiler not found."
}

$VersionDir = Join-Path $DistRoot $PackageName
$KoreanOutput = Join-Path $VersionDir "$AppName.exe"
$TempOutput = Join-Path $VersionDir "SmbManager.tmp.exe"
$SetupOutput = Join-Path $VersionDir "Setup.exe"
$SetupTempOutput = Join-Path $VersionDir "Setup.tmp.exe"
$UninstallOutput = Join-Path $VersionDir "Uninstall.exe"
$UninstallTempOutput = Join-Path $VersionDir "Uninstall.tmp.exe"
$AdminToolsDir = Join-Path $DistRoot ".admin-tools"
$ResetAdminOutput = Join-Path $AdminToolsDir "ResetAdminPassword.exe"
$ResetAdminTempOutput = Join-Path $AdminToolsDir "ResetAdminPassword.tmp.exe"

New-Item -ItemType Directory -Force -Path $VersionDir | Out-Null
New-Item -ItemType Directory -Force -Path $AdminToolsDir | Out-Null
Set-ItemProperty -LiteralPath $AdminToolsDir -Name Attributes -Value ([System.IO.FileAttributes]::Hidden)

& $Compiler /nologo /target:winexe /platform:anycpu /codepage:65001 `
    /out:$SetupTempOutput `
    "$Root\Setup.cs" `
    /reference:System.dll `
    /reference:System.Windows.Forms.dll `
    /reference:System.Drawing.dll

if ($LASTEXITCODE -ne 0) {
    throw "Setup build failed with exit code $LASTEXITCODE."
}

& $Compiler /nologo /target:winexe /platform:anycpu /codepage:65001 `
    /out:$UninstallTempOutput `
    "$Root\Uninstall.cs" `
    /reference:System.dll `
    /reference:System.Windows.Forms.dll `
    /reference:System.Drawing.dll

if ($LASTEXITCODE -ne 0) {
    throw "Uninstall build failed with exit code $LASTEXITCODE."
}

$SetupResourceArgument = "/resource:$SetupTempOutput,SmbManager.Bootstrap.Setup.exe"
$UninstallResourceArgument = "/resource:$UninstallTempOutput,SmbManager.Bootstrap.Uninstall.exe"
& $Compiler /nologo /target:winexe /platform:anycpu /codepage:65001 `
    /out:$TempOutput `
    $SetupResourceArgument `
    $UninstallResourceArgument `
    "$Root\Program.cs" `
    /reference:System.dll `
    /reference:System.Security.dll `
    /reference:System.Web.Extensions.dll `
    /reference:System.IO.Compression.dll `
    /reference:System.IO.Compression.FileSystem.dll `
    /reference:System.Windows.Forms.dll `
    /reference:System.Drawing.dll

if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE."
}

Move-Item -LiteralPath $TempOutput -Destination $KoreanOutput -Force
Move-Item -LiteralPath $SetupTempOutput -Destination $SetupOutput -Force
Move-Item -LiteralPath $UninstallTempOutput -Destination $UninstallOutput -Force

$ResetAdminSource = Join-Path $Root "ResetAdminPassword.cs"
if (Test-Path -LiteralPath $ResetAdminSource) {
    & $Compiler /nologo /target:winexe /platform:anycpu /codepage:65001 `
        /out:$ResetAdminTempOutput `
        $ResetAdminSource `
        /reference:System.dll `
        /reference:System.Windows.Forms.dll `
        /reference:System.Drawing.dll

    if ($LASTEXITCODE -ne 0) {
        throw "Reset admin password tool build failed with exit code $LASTEXITCODE."
    }

    Move-Item -LiteralPath $ResetAdminTempOutput -Destination $ResetAdminOutput -Force
}

$ReadmeSource = Join-Path $CurrentVersionDir "README.md"
$ReadmeDestination = Join-Path $VersionDir "README.md"
if ((Resolve-Path -LiteralPath $ReadmeSource).Path -ne (Resolve-Path -LiteralPath $ReadmeDestination -ErrorAction SilentlyContinue).Path) {
    Copy-Item -LiteralPath $ReadmeSource -Destination $ReadmeDestination -Force
}
$VersionSource = Join-Path $CurrentVersionDir "version.ini"
$VersionDestination = Join-Path $VersionDir "version.ini"
if (Test-Path -LiteralPath $VersionSource) {
    if ((Resolve-Path -LiteralPath $VersionSource).Path -ne (Resolve-Path -LiteralPath $VersionDestination -ErrorAction SilentlyContinue).Path) {
        Copy-Item -LiteralPath $VersionSource -Destination $VersionDestination -Force
    }
}
$FontsSource = Join-Path $RepositoryRoot "assets\fonts\noto-sans-cjk-kr"
if (!(Test-Path -LiteralPath $FontsSource)) { throw "Shared font assets are missing." }
$FontsDestination = Join-Path $VersionDir "Fonts"
if (Test-Path -LiteralPath $FontsSource) {
    if ((Resolve-Path -LiteralPath $FontsSource).Path -ne (Resolve-Path -LiteralPath $FontsDestination -ErrorAction SilentlyContinue).Path) {
        if (Test-Path -LiteralPath $FontsDestination) {
            $resolvedFonts = (Resolve-Path -LiteralPath $FontsDestination).Path
            if (!$resolvedFonts.StartsWith([IO.Path]::GetFullPath($DistRoot)+"\", [StringComparison]::OrdinalIgnoreCase) -or (Get-Item -LiteralPath $resolvedFonts -Force).Attributes -band [IO.FileAttributes]::ReparsePoint -or @(Get-ChildItem -LiteralPath $resolvedFonts -Force -Recurse | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw "Unsafe generated font directory." }
            Remove-Item -LiteralPath $resolvedFonts -Recurse -Force
        }
        Copy-Item -LiteralPath $FontsSource -Destination $FontsDestination -Recurse -Force
    }
}
Copy-Item -LiteralPath $Root -Destination $VersionDir -Recurse -Force
$HistoricalQA = Join-Path $CurrentVersionDir "REAL-RECONNECT-QA.md"
if (Test-Path -LiteralPath $HistoricalQA) { Copy-Item -LiteralPath $HistoricalQA -Destination $VersionDir -Force }

$LegacyUpdateManifest = Join-Path $VersionDir "latest.ini"
$LegacyBridgeRevision = "$Version"
$LegacyUpdateText = "Version=$LegacyBridgeRevision`r`nFileName=$AppName.exe`r`nMessage=정식 설치 방식으로 전환되는 업데이트입니다. 새 버전 실행 후 표시되는 설치 프로그램을 완료해 주세요.`r`n"
[System.IO.File]::WriteAllText($LegacyUpdateManifest, $LegacyUpdateText, (New-Object System.Text.UTF8Encoding($false)))

$ZipPath = Join-Path $DistRoot "$PackageName.zip"
if (Test-Path -LiteralPath $ZipPath) {
    Remove-Item -LiteralPath $ZipPath -Force
}
Compress-Archive -LiteralPath $VersionDir -DestinationPath $ZipPath -Force

Write-Host "Built: $KoreanOutput"
Write-Host "Built: $SetupOutput"
Write-Host "Built: $UninstallOutput"
if (Test-Path -LiteralPath $ResetAdminOutput) {
    Write-Host "Built admin tool: $ResetAdminOutput"
}
Write-Host "Packaged: $ZipPath"
Write-Host "Build log: $BuildLogPath"
Stop-Transcript | Out-Null
