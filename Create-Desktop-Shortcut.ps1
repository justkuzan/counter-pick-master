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
$link.IconLocation = "$exe,0"
$link.Save()
Write-Output $linkPath
