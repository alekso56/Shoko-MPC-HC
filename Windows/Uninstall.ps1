$ErrorActionPreference = 'Stop'
$installDir = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'ShokoMpcHandler'))
$expected = [IO.Path]::GetFullPath($env:LOCALAPPDATA).TrimEnd('\') + '\ShokoMpcHandler'
if ($installDir -ne $expected) { throw 'Unexpected installation path; uninstall cancelled.' }
$key = 'HKCU:\Software\Classes\shoko-mpc'
$exePath = Join-Path $installDir 'ShokoMpcHandler.exe'
$command = '"' + $exePath + '" "%1"'
if (Test-Path -LiteralPath ($key+'\shell\open\command')) {
    $existing = (Get-Item -LiteralPath ($key+'\shell\open\command')).GetValue('')
    if ($existing -ne $command) { throw 'Another application now owns this protocol. Nothing was removed.' }
    Remove-Item -LiteralPath $key -Recurse
}
foreach ($name in @('ShokoMpcHandler.exe','settings.json')) {
    $file = Join-Path $installDir $name
    if (Test-Path -LiteralPath $file -PathType Leaf) { Remove-Item -LiteralPath $file }
}
if ((Test-Path -LiteralPath $installDir) -and -not (Get-ChildItem -LiteralPath $installDir -Force)) { Remove-Item -LiteralPath $installDir }
Write-Host 'Shoko MPC-HC Windows integration removed.'
