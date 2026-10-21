param([string]$Package = (Join-Path $PSScriptRoot '..\artifacts\win-x64'))
$ErrorActionPreference = 'Stop'
$smokeRoot = Join-Path ([IO.Path]::GetTempPath()) ('OsuRelayPublishSmoke-' + [Guid]::NewGuid().ToString('N'))
$appDirectory = Join-Path $smokeRoot 'app'
$configDirectory = Join-Path $smokeRoot 'settings'
New-Item -ItemType Directory -Path $appDirectory, $configDirectory | Out-Null
$executable = Join-Path $appDirectory 'OsuSpectatorRelay.exe'
Copy-Item -LiteralPath (Join-Path $Package 'OsuSpectatorRelay.exe') -Destination $executable
$configPath = Join-Path $configDirectory 'config.json'
$settings = Get-Content -LiteralPath (Join-Path $Package 'config.example.json') -Raw | ConvertFrom-Json
$settings.osuPath = Join-Path $env:WINDIR 'System32\notepad.exe'
$settings.autoGenerateCandidates = $false
$settings.preferredIps = @()
$settings.workerHosts = @()
$settings.checkBeforeLaunch = $false
$portListener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$portListener.Start()
$settings.listenPort = $portListener.LocalEndpoint.Port
$portListener.Stop()
$settings | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $configPath -Encoding UTF8
$digest = [Security.Cryptography.SHA256]::Create()
try { $identity = [BitConverter]::ToString($digest.ComputeHash([Text.Encoding]::UTF8.GetBytes($configPath.ToUpperInvariant()))).Replace('-', '').Substring(0, 20) }
finally { $digest.Dispose() }
$pipeName = 'OsuSpectatorRelay-' + $identity
function Send-SmokeRequest([string]$Command) {
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $pipeName, [IO.Pipes.PipeDirection]::InOut)
    try {
        $pipe.Connect(500)
        $writer = [IO.StreamWriter]::new($pipe, [Text.UTF8Encoding]::new($false), 1024, $true)
        $reader = [IO.StreamReader]::new($pipe, [Text.Encoding]::UTF8, $false, 1024, $true)
        try {
            $writer.AutoFlush = $true
            $writer.WriteLine(('{"Command":"' + $Command + '","Pid":0}'))
            $reading = $reader.ReadLineAsync()
            if (-not $reading.Wait(3000)) { throw 'IPC response timeout' }
            return $reading.Result | ConvertFrom-Json
        } finally { $writer.Dispose(); $reader.Dispose() }
    } finally { $pipe.Dispose() }
}
$service = $null
try {
    $shortcutProcess = Start-Process -FilePath $executable -ArgumentList @('shortcut', '--config', ('"' + $configPath + '"')) -WindowStyle Hidden -PassThru
    if (-not $shortcutProcess.WaitForExit(10000)) { $shortcutProcess.Kill(); throw 'Shortcut process timed out' }
    if ($shortcutProcess.ExitCode -ne 0) { throw 'Published shortcut creation failed' }
    $shortcutPath = Join-Path $appDirectory 'osu! (Relay).lnk'
    if (-not (Test-Path -LiteralPath $shortcutPath) -or (Test-Path -LiteralPath (Join-Path $configDirectory 'osu! (Relay).lnk'))) {
        throw 'Shortcut must be created beside EXE, independent of config directory'
    }
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    try {
        if ($shortcut.TargetPath -ne $executable -or $shortcut.Arguments -ne ('start --config "' + $configPath + '"') -or
            $shortcut.IconLocation -ne ($settings.osuPath + ',0')) { throw 'Shortcut target, arguments or osu icon mismatch' }
    } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut); [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
    $saved = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    if ($saved.sharedSecret.Length -ne 64) { throw 'Trimmed config initialization failed' }
    $configHash = (Get-FileHash -LiteralPath $configPath).Hash
    $service = Start-Process -FilePath $executable -ArgumentList @('serve', '--config', ('"' + $configPath + '"')) -WindowStyle Hidden -PassThru
    $status = $null
    for ($attempt = 0; $attempt -lt 20 -and $null -eq $status; $attempt++) {
        try { $status = Send-SmokeRequest 'ping' }
        catch [TimeoutException] { Start-Sleep -Milliseconds 100 }
    }
    if ($null -eq $status -or $status.Pid -ne $service.Id -or $status.Port -ne $settings.listenPort) { throw 'Published service readiness failed' }
    Start-Sleep -Milliseconds 2200
    $statusFile = Get-Content -LiteralPath (Join-Path $configDirectory 'runtime\status.json') -Raw | ConvertFrom-Json
    if ($statusFile.pid -ne $service.Id) { throw 'Trimmed status serialization failed' }
    [void](Send-SmokeRequest 'stop')
    if (-not $service.WaitForExit(8000) -or $service.ExitCode -ne 0) { throw 'Published service shutdown failed' }
    if ((Get-FileHash -LiteralPath $configPath).Hash -ne $configHash) { throw 'Existing configuration changed' }
    Write-Output 'PASS published EXE: shortcut beside EXE, osu icon, generated/preserved key, hidden service, IPC, status JSON and clean shutdown'
    Write-Output ('Smoke files: ' + $smokeRoot)
} finally {
    if ($null -ne $service -and -not $service.HasExited) { $service.Kill(); $service.WaitForExit() }
}
