param([switch]$SkipBuild,[string]$CandidateRoot=(Join-Path $PSScriptRoot '.local\beta1'))
$ErrorActionPreference = 'Stop'
$sourceBytes=[IO.File]::ReadAllBytes($PSCommandPath)
if($sourceBytes.Length -lt 3 -or $sourceBytes[0] -ne 239 -or $sourceBytes[1] -ne 187 -or $sourceBytes[2] -ne 191){throw 'Windows PowerShell UI QA requires UTF-8 with BOM. Refusing corrupted test names.'}
if (!$SkipBuild) { & (Join-Path $PSScriptRoot 'Build.ps1') -OutputRoot $CandidateRoot }
$run = Join-Path $PSScriptRoot ('.local\ui-qa\' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,6))
New-Item -ItemType Directory -Path $run -Force | Out-Null
$started = @()
try {
    $server = Start-Process -FilePath (Join-Path $CandidateRoot 'bin\ManagementServer\Release\net10.0\ManagementServer.exe') -ArgumentList @('--run-dir', ('"' + $run + '"')) -WindowStyle Hidden -PassThru
    $started += $server
    $readyFile = Join-Path $run 'ready.json'
    $deadline = (Get-Date).AddSeconds(20)
    while (!(Test-Path -LiteralPath $readyFile)) {
        if ($server.HasExited -or (Get-Date) -gt $deadline) { throw 'UI 검사 서버 시작 실패' }
        Start-Sleep -Milliseconds 100
    }
    $ready = Get-Content -LiteralPath $readyFile -Raw | ConvertFrom-Json
    $headers = @{ 'X-Admin-Key' = $ready.AdminKey }
    foreach ($size in @('default','minimum')) {
        $code = Invoke-RestMethod -Method Post -Uri ($ready.Url + 'api/admin/enrollment-code') -Headers $headers
        $bootstrap = Join-Path $run ('client-' + $size + '-bootstrap.json')
        @{url=$ready.Url; code=$code.code; name=('화면 검사 ' + $size)} | ConvertTo-Json | Set-Content -LiteralPath $bootstrap -Encoding utf8
        $snapshot = Join-Path $run ('client-' + $size + '.png')
        $launchArgs = @('--bootstrap', ('"' + $bootstrap + '"'), '--state-dir', ('"' + (Join-Path $run ('client-' + $size)) + '"'), '--snapshot', ('"' + $snapshot + '"'))
        if ($size -eq 'minimum') { $launchArgs += '--minimum-size' }
        $p = Start-Process -FilePath (Join-Path $CandidateRoot 'bin\ClientDesktop\Release\net10.0-windows\ClientDesktop.exe') -ArgumentList $launchArgs -PassThru
        $processHandle=$p.Handle;$started += $p
        $deadline = (Get-Date).AddSeconds(8)
        do {
            $devices = Invoke-RestMethod -Uri ($ready.Url + 'api/admin/devices') -Headers $headers
            $device = $devices | Where-Object { $_.name -eq ('화면 검사 ' + $size) }
            if (!$device) { Start-Sleep -Milliseconds 100 }
        } while (!$device -and (Get-Date) -lt $deadline)
        if (!$device) { throw '화면 검사 Client 등록 실패' }
        Invoke-RestMethod -Method Post -Uri ($ready.Url + 'api/admin/devices/' + $device.id + '/approve') -Headers $headers | Out-Null
        $body = @{deviceId=$device.id; profileId='demo-share'; ttlSeconds=60} | ConvertTo-Json
        Invoke-RestMethod -Method Post -Uri ($ready.Url + 'api/admin/commands') -Headers $headers -ContentType 'application/json; charset=utf-8' -Body $body | Out-Null
        if (!$p.WaitForExit(25000) -or $p.ExitCode -ne 0) { throw ('Client 화면 검사 실패: ' + $size) }
        $layout = Get-Content -LiteralPath ($snapshot + '.layout.json') -Raw | ConvertFrom-Json
        if ($layout.problems.Count -ne 0) { throw ('Client 레이아웃 오류: ' + $size) }
        Write-Output ('PASS Client ' + $size + ': ' + $layout.width + 'x' + $layout.height + ', DPI ' + $layout.dpi)
    }
    foreach ($size in @('default','minimum')) {
        $snapshot = Join-Path $run ('admin-' + $size + '.png')
        $launchArgs = @('--ready', ('"' + $readyFile + '"'), '--snapshot', ('"' + $snapshot + '"'))
        if ($size -eq 'minimum') { $launchArgs += '--minimum-size' }
        $p = Start-Process -FilePath (Join-Path $CandidateRoot 'bin\AdminDesktop\Release\net10.0-windows\AdminDesktop.exe') -ArgumentList $launchArgs -PassThru
        $processHandle=$p.Handle;$started += $p
        if (!$p.WaitForExit(25000) -or $p.ExitCode -ne 0) { throw ('Admin 화면 검사 실패: ' + $size) }
        $layout = Get-Content -LiteralPath ($snapshot + '.layout.json') -Raw | ConvertFrom-Json
        if ($layout.problems.Count -ne 0) { throw ('Admin 레이아웃 오류: ' + $size) }
        Write-Output ('PASS Admin ' + $size + ': ' + $layout.width + 'x' + $layout.height + ', DPI ' + $layout.dpi)
    }
    foreach($size in @('default','minimum')){
        $snapshot=Join-Path $run ('assignment-'+$size+'.png')
        $launchArgs=@('--assignment-preview','--snapshot',('"'+$snapshot+'"'))
        if($size -eq 'minimum'){$launchArgs+='--minimum-size'}
        $p=Start-Process -FilePath (Join-Path $CandidateRoot 'bin\AdminDesktop\Release\net10.0-windows\AdminDesktop.exe') -ArgumentList $launchArgs -PassThru
        $processHandle=$p.Handle;$started+=$p
        if(!$p.WaitForExit(25000) -or $p.ExitCode -ne 0){throw ('Assignment UI failed: '+$size+'; exit='+$p.ExitCode+'; exited='+$p.HasExited)}
        $layout=Get-Content ($snapshot+'.layout.json') -Raw|ConvertFrom-Json
        if($layout.problems.Count -ne 0){throw 'Assignment layout errors'}
        Write-Output ('PASS Assignment '+$size+': '+$layout.width+'x'+$layout.height+', DPI '+$layout.dpi)
    }
    foreach($mode in @('profiles','notification','departments')) {
        foreach($size in @('default','minimum')) {
            $snapshot=Join-Path $run ($mode+'-'+$size+'.png')
            $launchArgs=@(('--'+$mode+'-preview'),'--snapshot',('"'+$snapshot+'"'))
            if($size -eq 'minimum'){$launchArgs+='--minimum-size'}
            $p=Start-Process -FilePath (Join-Path $CandidateRoot 'bin\AdminDesktop\Release\net10.0-windows\AdminDesktop.exe') -ArgumentList $launchArgs -PassThru
            $processHandle=$p.Handle;$started+=$p
            if(!$p.WaitForExit(25000) -or $p.ExitCode -ne 0){throw ('Dialog UI failed: '+$mode+' '+$size)}
            $layout=Get-Content ($snapshot+'.layout.json') -Raw|ConvertFrom-Json
            if($layout.problems.Count -ne 0){throw ('Dialog layout errors: '+$mode)}
            Write-Output ('PASS '+$mode+' '+$size+': '+$layout.width+'x'+$layout.height+', DPI '+$layout.dpi)
        }
    }
    foreach($scale in @('1.25','1.5','2')) {
        foreach($project in @('ClientDesktop','AdminDesktop')) {
            $snapshot=Join-Path $run ($project+'-scale-'+$scale+'.png')
            $launchArgs=@('--snapshot',('"'+$snapshot+'"'),'--minimum-size','--render-scale',$scale)
            if($project -eq 'ClientDesktop'){$launchArgs+=@('--state-dir',('"'+(Join-Path $run ('scaled-client-'+$scale))+'"'))}else{$launchArgs+=@('--ready',('"'+$readyFile+'"'))}
            $p=Start-Process -FilePath (Join-Path $CandidateRoot ('bin\'+$project+'\Release\net10.0-windows\'+$project+'.exe')) -ArgumentList $launchArgs -PassThru
            $processHandle=$p.Handle;$started+=$p
            if(!$p.WaitForExit(15000) -or $p.ExitCode -ne 0){throw ('Enlarged layout failed: '+$project+' '+$scale)}
            Write-Output ('PASS font/geometry stress '+$project+' x'+$scale+' (not real OS DPI)')
        }
    }
    Set-Content -LiteralPath (Join-Path $PSScriptRoot '.local\last-ui-qa.txt') -Value $run -NoNewline -Encoding utf8
    Write-Output ('화면 검사 기록: ' + $run)
} finally {
    foreach ($p in $started) { try { if (!$p.HasExited) { Stop-Process -Id $p.Id -ErrorAction SilentlyContinue } } catch { } }
}
