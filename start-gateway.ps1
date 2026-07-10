param(
  [switch]$NonInteractive,
  [switch]$DebugGateway,
  [string]$Token,
  [string]$Tenant = "5282fa6645"
)

$ErrorActionPreference = "Stop"

Set-Location -LiteralPath $PSScriptRoot

if (-not $NonInteractive -and [string]::IsNullOrWhiteSpace($Token)) {
  $Token = Read-Host "Catpaw access token (blank: read Catpaw state)"
}
$autoToken = [string]::IsNullOrWhiteSpace($Token)
$state = $null
if ($autoToken) {
  $state = node src/catpawState.js | ConvertFrom-Json
  $Token = $state.token
}
if ([string]::IsNullOrWhiteSpace($Token)) {
  throw "Catpaw access token is required"
}

if (-not $NonInteractive) {
  $tenantInput = Read-Host "Catpaw tenant (default: $Tenant)"
  if (-not [string]::IsNullOrWhiteSpace($tenantInput)) {
    $Tenant = $tenantInput
  }
}

$userMis = if ($state) { $state.userMis } else { "" }

$existing = Get-NetTCPConnection -LocalPort 3000 -ErrorAction SilentlyContinue | Select-Object -First 1
if ($existing) {
  $process = Get-CimInstance Win32_Process -Filter "ProcessId = $($existing.OwningProcess)" -ErrorAction SilentlyContinue
  if ($process -and $process.Name -eq "node.exe" -and $process.CommandLine -like "*src/server.js*") {
    Stop-Process -Id $existing.OwningProcess -Force
  } else {
    throw "Port 3000 is already in use by PID $($existing.OwningProcess). Stop it first or run the gateway on another port."
  }
}

$env:CATPAW_BASE_URL = "https://catpaw.meituan.com"
$env:CATPAW_UPSTREAM_URL = "https://catpaw.meituan.com/api/gpt/openai/stream"
$env:CATPAW_MODEL = "glm-5.2"
$env:CATPAW_AUTH_TOKEN = $Token
$env:CATPAW_COOKIE = "1d47d6ff96_passportid=$Token; f32a546874_ssoid=$Token"
$env:CATPAW_TENANT = $Tenant
$env:CATPAW_USER_MIS_ID = $userMis
$env:CATPAW_ENCRYPT = "1"
$env:CATPAW_FORCE_STREAM = "1"
$env:CATPAW_NATIVE_AGENT = "1"
$env:CATPAW_AUTO_REFRESH_TOKEN = if ($autoToken) { "1" } else { "0" }
$env:CATPAW_MODEL_TYPE = "2"
$env:CATPAW_DEBUG = if ($DebugGateway) { "1" } else { "0" }
$env:CATPAW_HEADERS = '{"ide-type":"CatPaw IDE","client-type":"CatPaw IDE","ide-version":"2026.2.3","plugin-id":"mt-idekit.mt-idekit-code","plugin-version":"2026.2.2","client-env":"LOCAL_IDE","platform-info":"win32-x64","UI-Version":"0.2.2"}'

npm start
