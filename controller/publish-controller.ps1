$ErrorActionPreference = "Stop"

$project = Join-Path $PSScriptRoot "CatapiController\CatapiController.csproj"
$output = Join-Path $PSScriptRoot "publish"

if (Test-Path -LiteralPath $output) {
  Remove-Item -LiteralPath $output -Recurse -Force
}

dotnet publish $project -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:DebugType=None -p:DebugSymbols=false -o $output

$executable = Join-Path $output "ThiefNeko.exe"
if (-not (Test-Path -LiteralPath $executable)) {
  throw "ThiefNeko.exe was not created"
}

Write-Host $executable
