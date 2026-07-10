$ErrorActionPreference = "Stop"

$target = Join-Path $PSScriptRoot "publish\ThiefNeko.exe"
if (-not (Test-Path -LiteralPath $target)) {
  throw "Run publish-controller.ps1 first"
}

$desktop = [Environment]::GetFolderPath("Desktop")
$shortcutPath = Join-Path $desktop "Thief Neko.lnk"
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $target
$shortcut.WorkingDirectory = Split-Path $PSScriptRoot -Parent
$shortcut.Description = "Catpaw gateway controller"
$shortcut.Save()

Write-Host $shortcutPath
