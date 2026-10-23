param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts'), [switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Build on Windows with .NET Framework 4.x and the .NET 10 SDK installed.' }
$windowsDir = Join-Path $PSScriptRoot 'Windows'
$handler = Join-Path $windowsDir 'ShokoMpcHandler.exe'
$source = Join-Path $windowsDir 'Handler.cs'
& $compiler /nologo /target:winexe "/out:$handler" /r:System.Net.Http.dll /r:System.Web.dll /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll $source
if ($LASTEXITCODE) { throw 'Windows handler compilation failed.' }
$embeddedDir = Join-Path $PSScriptRoot 'ShokoMpc\Windows'
New-Item -ItemType Directory -Path $embeddedDir,$OutputDirectory -Force | Out-Null
$setupFiles = @('ShokoMpcHandler.exe','Install.ps1','Install.cmd','Uninstall.ps1','Uninstall.cmd') | ForEach-Object {Join-Path $windowsDir $_}
Compress-Archive -LiteralPath $setupFiles -DestinationPath (Join-Path $embeddedDir 'Windows-Setup.zip') -Force
dotnet build (Join-Path $PSScriptRoot 'ShokoMpc\ShokoMpc.csproj') -c Release --nologo
if ($LASTEXITCODE) { throw 'Plugin compilation failed.' }
if (-not $SkipTests) {
    dotnet run --project (Join-Path $PSScriptRoot 'Tests\Tests.csproj') -c Release
    if ($LASTEXITCODE) { throw 'Plugin tests failed.' }
    $testSource = Join-Path $PSScriptRoot 'Tests\WindowsProtocolTests.cs'
    $testDir = Join-Path $PSScriptRoot 'artifacts'
    New-Item -ItemType Directory -Path $testDir -Force | Out-Null
    $testExe = Join-Path $testDir 'WindowsProtocolTests.exe'
    & $compiler /nologo /target:exe /main:WindowsProtocolTests "/out:$testExe" /r:System.Net.Http.dll /r:System.Web.dll /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll $source $testSource
    if ($LASTEXITCODE) { throw 'Windows tests failed to compile.' }
    & $testExe
    if ($LASTEXITCODE) { throw 'Windows tests failed.' }
}
$archiveName = 'ShokoMpc-v0.1.1-any.zip'
$dll = Join-Path $PSScriptRoot 'ShokoMpc\bin\Release\net10.0\ShokoMpc.dll'
Compress-Archive -LiteralPath $dll -DestinationPath (Join-Path $OutputDirectory $archiveName) -Force
Copy-Item -LiteralPath (Join-Path $embeddedDir 'Windows-Setup.zip') -Destination (Join-Path $OutputDirectory 'ShokoMpc-Windows-Setup.zip') -Force
$checksum = (Get-FileHash -LiteralPath (Join-Path $OutputDirectory $archiveName) -Algorithm SHA256).Hash.ToLowerInvariant()
$manifest = [ordered]@{
    id='e84bc763-559a-4a54-9332-69741ec61c25'; name='Shoko MPC-HC';
    overview='Play episodes in MPC-HC directly from the Shoko Web UI. Includes one-time Windows link-handler setup.';
    authors='alekso56'; repository_url='https://github.com/alekso56/Shoko-MPC-HC'; homepage_url=$null;
    tags=@('media-player','mpc-hc','webui'); thumbnail=$null; icon=$null;
    releases=@([ordered]@{version='0.1.1'; tag='v0.1.1'; source_revision=$null; released_at='2026-10-24T00:00:00Z'; channel='Stable';
        release_notes='Fix session-only WebUI sign-ins. Targets Shoko 6 dev.550 / Abstractions alpha.112. Requires one-time Windows setup. No watch-state sync or external subtitle handoff.';
        archives=@([ordered]@{runtime='any'; abstraction='6.0.0'; url="https://github.com/alekso56/Shoko-MPC-HC/releases/download/v0.1.1/$archiveName"; checksum=$checksum})})
}
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'manifest.json') -Encoding UTF8
Write-Host "Built $archiveName (SHA256 $checksum)"
