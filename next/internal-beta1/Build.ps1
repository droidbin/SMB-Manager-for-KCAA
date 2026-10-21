param([string]$OutputRoot = (Join-Path $PSScriptRoot '.local\beta1'))
$ErrorActionPreference = 'Stop'
$PrototypeRoot = $PSScriptRoot
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
$env:DOTNET_CLI_HOME = Join-Path $PrototypeRoot '.local\dotnet-home'
& dotnet build (Join-Path $PrototypeRoot 'LocalPrototype.slnx') -c Release --ignore-failed-sources -p:NuGetAudit=false "-p:SmbOutputRoot=$OutputRoot"
if ($LASTEXITCODE -ne 0) { throw '로컬 시제품 빌드에 실패했습니다.' }
