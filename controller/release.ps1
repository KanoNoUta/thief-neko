param(
  [string]$Version = "0.2.0"
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $root "dist"
$winStage = Join-Path $dist "Thief-Neko-v$Version-win-x64"
$linuxStage = Join-Path $dist "Thief-Neko-v$Version-linux-x64"
$winArchive = Join-Path $dist "Thief-Neko-v$Version-win-x64.zip"
$linuxArchive = Join-Path $dist "Thief-Neko-v$Version-linux-x64.tar.gz"

if (Test-Path -LiteralPath $dist) {
  Remove-Item -LiteralPath $dist -Recurse -Force
}
New-Item -ItemType Directory -Path $dist -Force | Out-Null

& (Join-Path $PSScriptRoot "publish-controller.ps1")

New-Item -ItemType Directory -Path $winStage -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "publish\ThiefNeko.exe") -Destination $winStage
Copy-Item -LiteralPath (Join-Path $root "src") -Destination $winStage -Recurse
Copy-Item -LiteralPath (Join-Path $root "assets") -Destination $winStage -Recurse
Copy-Item -LiteralPath (Join-Path $root "package.json") -Destination $winStage
Copy-Item -LiteralPath (Join-Path $root "README.md") -Destination $winStage
Copy-Item -LiteralPath (Join-Path $root "LICENSE") -Destination $winStage
Copy-Item -LiteralPath (Join-Path $root "start-gateway.ps1") -Destination $winStage
New-Item -ItemType Directory -Path (Join-Path $winStage "docs") -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root "docs\NEW-API.md") -Destination (Join-Path $winStage "docs")

New-Item -ItemType Directory -Path $linuxStage -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root "src") -Destination $linuxStage -Recurse
Copy-Item -LiteralPath (Join-Path $root "package.json") -Destination $linuxStage
Copy-Item -LiteralPath (Join-Path $root "README.md") -Destination $linuxStage
Copy-Item -LiteralPath (Join-Path $root "LICENSE") -Destination $linuxStage
New-Item -ItemType Directory -Path (Join-Path $linuxStage "docs") -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root "docs\NEW-API.md") -Destination (Join-Path $linuxStage "docs")

Compress-Archive -Path (Join-Path $winStage "*") -DestinationPath $winArchive -CompressionLevel Optimal
tar.exe -czf $linuxArchive -C $linuxStage .

Remove-Item -LiteralPath $winStage -Recurse -Force
Remove-Item -LiteralPath $linuxStage -Recurse -Force

foreach ($archive in @($winArchive, $linuxArchive)) {
  Get-Item $archive | Select-Object FullName, Length
  Get-FileHash -Algorithm SHA256 $archive | Select-Object Algorithm, Hash
}
