param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts'), [switch]$SkipTests)
$ErrorActionPreference = 'Stop'
dotnet build (Join-Path $PSScriptRoot 'ShokoSeasons\ShokoSeasons.csproj') -c Release --nologo
if ($LASTEXITCODE) { throw 'Seasonal plugin compilation failed.' }
if (-not $SkipTests) {
    dotnet run --project (Join-Path $PSScriptRoot 'SeasonsTests\SeasonsTests.csproj') -c Release
    if ($LASTEXITCODE) { throw 'Seasonal backend tests failed.' }
    node (Join-Path $PSScriptRoot 'Tests\seasons.cjs')
    if ($LASTEXITCODE) { throw 'Seasonal DOM tests failed. Install Tests/package.json dependencies first.' }
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$version = '0.1.1'
$tag = "seasons-v$version"
$archiveName = "ShokoSeasons-v$version-any.zip"
$dll = Join-Path $PSScriptRoot 'ShokoSeasons\bin\Release\net10.0\ShokoSeasons.dll'
Compress-Archive -LiteralPath $dll -DestinationPath (Join-Path $OutputDirectory $archiveName) -Force
$checksum = (Get-FileHash -LiteralPath (Join-Path $OutputDirectory $archiveName) -Algorithm SHA256).Hash.ToLowerInvariant()
$manifest = [ordered]@{
    id='7b22d3ef-125d-4dca-9006-b7d256c04e03'; name='Shoko Seasons';
    overview='Browse seasonal anime, add seasons to Sonarr, and follow downloads into Shoko with saved ready notifications.';
    authors='alekso56'; repository_url='https://github.com/alekso56/Shoko-MPC-HC'; homepage_url=$null;
    tags=@('seasonal','anime','sonarr','webui'); thumbnail=$null; icon=$null;
    releases=@([ordered]@{version=$version; tag=$tag; source_revision=$null; released_at='2026-11-08T00:00:00Z'; channel='Stable';
        release_notes='Disable Add to list for anime already in Shoko and enforce the same rule on the server. Keep episode links available. Requires configured Shokarr and Shoko 6 dev.550 / Abstractions alpha.112.';
        archives=@([ordered]@{runtime='any'; abstraction='6.0.0'; url="https://github.com/alekso56/Shoko-MPC-HC/releases/download/$tag/$archiveName"; checksum=$checksum})})
}
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'manifest.seasons.json') -Encoding UTF8
Write-Host "Built $archiveName (SHA256 $checksum)"
