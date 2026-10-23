param([string]$ServerUrl, [string]$PlayerPath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
if (-not $ServerUrl) { $ServerUrl = Read-Host 'Shoko server URL (example: http://shoko-server:8111)' }
$serverUri = $null
if (-not [Uri]::TryCreate($ServerUrl.TrimEnd('/'), [UriKind]::Absolute, [ref]$serverUri) -or
    $serverUri.Scheme -notin @('http','https') -or $serverUri.UserInfo -or $serverUri.Query -or $serverUri.Fragment -or $ServerUrl.Contains('\')) {
    throw 'Enter a valid http:// or https:// Shoko address, with its port and without a query or fragment.'
}
if (-not $PlayerPath) {
    $candidates = @("$env:ProgramFiles\MPC-HC\mpc-hc64.exe", "$env:ProgramFiles\MPC-HC (64-bit)\mpc-hc64.exe", "${env:ProgramFiles(x86)}\MPC-HC\mpc-hc.exe")
    $PlayerPath = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $PlayerPath) {
        $picker = New-Object System.Windows.Forms.OpenFileDialog
        $picker.Title = 'Select MPC-HC'; $picker.Filter = 'MPC-HC|mpc-hc64.exe;mpc-hc.exe'
        if ($picker.ShowDialog() -ne 'OK') { throw 'Setup cancelled.' }
        $PlayerPath = $picker.FileName
    }
}
if (-not (Test-Path -LiteralPath $PlayerPath -PathType Leaf) -or [IO.Path]::GetFileName($PlayerPath) -notin @('mpc-hc.exe','mpc-hc64.exe')) { throw 'Select your MPC-HC executable.' }
$installDir = Join-Path $env:LOCALAPPDATA 'ShokoMpcHandler'
$key = 'HKCU:\Software\Classes\shoko-mpc'
$exePath = Join-Path $installDir 'ShokoMpcHandler.exe'
$command = '"' + $exePath + '" "%1"'
if (Test-Path -LiteralPath $key) {
    $existing = (Get-Item -LiteralPath ($key+'\shell\open\command') -ErrorAction SilentlyContinue).GetValue('')
    if ($existing -and $existing -ne $command) { throw 'Another application owns shoko-mpc links. Setup did not overwrite it.' }
}
New-Item -ItemType Directory -Path $installDir -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ShokoMpcHandler.exe') -Destination $exePath -Force
@{ ServerUrl = $serverUri.AbsoluteUri.TrimEnd('/'); PlayerPath = (Resolve-Path -LiteralPath $PlayerPath).Path } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $installDir 'settings.json') -Encoding UTF8
New-Item -Path ($key+'\shell\open\command') -Force | Out-Null
Set-Item -LiteralPath $key -Value 'URL:Shoko MPC-HC'
New-ItemProperty -LiteralPath $key -Name 'URL Protocol' -Value '' -PropertyType String -Force | Out-Null
Set-Item -LiteralPath ($key+'\shell\open\command') -Value $command
Write-Host "Installed for $($serverUri.AbsoluteUri). Refresh Shoko, then click an episode's MPC-HC button."
