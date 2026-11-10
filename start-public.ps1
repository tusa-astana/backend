$ErrorActionPreference = 'Stop'

$publicApiUrl = $env:TUSA_PUBLIC_API_URL
if ([string]::IsNullOrWhiteSpace($publicApiUrl)) {
  throw 'Set TUSA_PUBLIC_API_URL, for example https://api.example.kz'
}

if (-not (Get-Command cloudflared -ErrorAction SilentlyContinue)) {
  throw 'Install cloudflared first: https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/downloads/'
}

$env:PORT = '5001'
$env:Cors__Origins__0 = 'https://tusa-astana.github.io'

Write-Host "Starting TUSA API on http://localhost:5001"
Write-Host "Public URL must point to this machine: $publicApiUrl"

Start-Process powershell -ArgumentList '-NoExit', '-Command', 'dotnet run --project C:\projects\backend\TusaMap.Api\TusaMap.Api.csproj'
cloudflared tunnel run tusa-api
