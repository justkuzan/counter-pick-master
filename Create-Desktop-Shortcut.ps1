$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'CounterPickMaster.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw "CounterPickMaster.exe must be next to this script."
}
$desktop = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)
$linkPath = Join-Path $desktop 'Counter Pick Master.lnk'
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($linkPath)
$link.TargetPath = $exe
$link.WorkingDirectory = $PSScriptRoot
$icon = Join-Path $PSScriptRoot 'CounterPickMaster.ico'
$link.IconLocation = if (Test-Path -LiteralPath $icon -PathType Leaf) { "$icon,0" } else { "$exe,0" }
$link.Save()
Write-Output $linkPath
