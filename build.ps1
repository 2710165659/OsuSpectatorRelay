param(
    [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64',
    [switch]$FrameworkDependent
)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$outputPath = Join-Path $projectRoot "artifacts\$Runtime"
$projectFile = Join-Path $projectRoot 'src\OsuSpectatorRelay\OsuSpectatorRelay.csproj'
$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
$trim = if ($FrameworkDependent) { 'false' } else { 'true' }
dotnet publish $projectFile -c Release -r $Runtime --self-contained $selfContained -p:PublishSingleFile=true -p:PublishTrimmed=$trim -p:TrimMode=full -p:EnableCompressionInSingleFile=true -p:DebugType=None -o $outputPath
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
# Preserve the user's configuration and key across rebuilds.
$configPath = Join-Path $outputPath 'config.json'
if (-not (Test-Path -LiteralPath $configPath)) {
    $settings = Get-Content -LiteralPath (Join-Path $projectRoot 'config.example.json') -Raw | ConvertFrom-Json
    $secretBytes = New-Object byte[] 32
    $generator = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $generator.GetBytes($secretBytes) } finally { $generator.Dispose() }
    $settings.sharedSecret = [BitConverter]::ToString($secretBytes).Replace('-', '')
    $settings | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $configPath -Encoding UTF8
}
$obsoleteDeploymentConfig = Join-Path $outputPath 'worker\wrangler.toml'
if (Test-Path -LiteralPath $obsoleteDeploymentConfig) { Remove-Item -LiteralPath $obsoleteDeploymentConfig }
Write-Output "Build ready: $outputPath"
