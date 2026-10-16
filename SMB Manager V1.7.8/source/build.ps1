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
$DistRoot = Split-Path -Parent $CurrentVersionDir
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
$FontsSource = Join-Path $CurrentVersionDir "Fonts"
$FontsDestination = Join-Path $VersionDir "Fonts"
if (Test-Path -LiteralPath $FontsSource) {
    if ((Resolve-Path -LiteralPath $FontsSource).Path -ne (Resolve-Path -LiteralPath $FontsDestination -ErrorAction SilentlyContinue).Path) {
        if (Test-Path -LiteralPath $FontsDestination) {
            Remove-Item -LiteralPath $FontsDestination -Recurse -Force
        }
        Copy-Item -LiteralPath $FontsSource -Destination $FontsDestination -Recurse -Force
    }
}
New-Item -ItemType Directory -Force -Path (Join-Path $VersionDir "source") | Out-Null
$ProgramSource = Join-Path $Root "Program.cs"
$ProgramDestination = Join-Path $VersionDir "source\Program.cs"
if ((Resolve-Path -LiteralPath $ProgramSource).Path -ne (Resolve-Path -LiteralPath $ProgramDestination -ErrorAction SilentlyContinue).Path) {
    Copy-Item -LiteralPath $ProgramSource -Destination $ProgramDestination -Force
}
$BuildSource = Join-Path $Root "build.ps1"
$BuildDestination = Join-Path $VersionDir "source\build.ps1"
if ((Resolve-Path -LiteralPath $BuildSource).Path -ne (Resolve-Path -LiteralPath $BuildDestination -ErrorAction SilentlyContinue).Path) {
    Copy-Item -LiteralPath $BuildSource -Destination $BuildDestination -Force
}
$SetupSource = Join-Path $Root "Setup.cs"
$SetupDestination = Join-Path $VersionDir "source\Setup.cs"
if ((Resolve-Path -LiteralPath $SetupSource).Path -ne (Resolve-Path -LiteralPath $SetupDestination -ErrorAction SilentlyContinue).Path) {
    Copy-Item -LiteralPath $SetupSource -Destination $SetupDestination -Force
}
$UninstallSource = Join-Path $Root "Uninstall.cs"
$UninstallDestination = Join-Path $VersionDir "source\Uninstall.cs"
if ((Resolve-Path -LiteralPath $UninstallSource).Path -ne (Resolve-Path -LiteralPath $UninstallDestination -ErrorAction SilentlyContinue).Path) {
    Copy-Item -LiteralPath $UninstallSource -Destination $UninstallDestination -Force
}

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
