$ErrorActionPreference = 'Stop'
dotnet run --project (Join-Path $PSScriptRoot 'tests\OsuSpectatorRelay.Tests\OsuSpectatorRelay.Tests.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'C# integration tests failed' }
node --test (Join-Path $PSScriptRoot 'tests\worker.test.mjs')
if ($LASTEXITCODE -ne 0) { throw 'Worker tests failed' }
