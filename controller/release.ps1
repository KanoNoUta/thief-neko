param(
  [string]$Version = "0.1.0"
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $root "dist"
$stage = Join-Path $dist "Thief-Neko"
$archive = Join-Path $dist "Thief-Neko-v$Version-win-x64.zip"

& (Join-Path $PSScriptRoot "publish-controller.ps1")

if (Test-Path -LiteralPath $stage) {
  Remove-Item -LiteralPath $stage -Recurse -Force
}
New-Item -ItemType Directory -Path $stage -Force | Out-Null

Copy-Item -LiteralPath (Join-Path $PSScriptRoot "publish\ThiefNeko.exe") -Destination $stage
Copy-Item -LiteralPath (Join-Path $root "src") -Destination $stage -Recurse
Copy-Item -LiteralPath (Join-Path $root "assets") -Destination $stage -Recurse
Copy-Item -LiteralPath (Join-Path $root "package.json") -Destination $stage
Copy-Item -LiteralPath (Join-Path $root "README.md") -Destination $stage
Copy-Item -LiteralPath (Join-Path $root "LICENSE") -Destination $stage
Copy-Item -LiteralPath (Join-Path $root "start-gateway.ps1") -Destination $stage

if (Test-Path -LiteralPath $archive) {
  Remove-Item -LiteralPath $archive -Force
}
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $archive -CompressionLevel Optimal

Get-Item $archive | Select-Object FullName, Length
Get-FileHash -Algorithm SHA256 $archive | Select-Object Algorithm, Hash
