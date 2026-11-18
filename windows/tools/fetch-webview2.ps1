# Download the WebView2 SDK NuGet package into windows\vendor\webview2.
# Run once; dist\ and vendor\ are git-ignored.
# ASCII-only: Windows PowerShell 5.1 misreads BOM-less UTF-8 comments.
param([string]$Version = '1.0.2592.51')
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($Version)) { $Version = '1.0.2592.51' }

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$vendor = Join-Path $root 'vendor\webview2'
$tmp = Join-Path $env:TEMP ("quickask-webview2-$Version")
$zip = Join-Path $env:TEMP ("quickask-webview2-$Version.zip")
$url = "https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/$Version/microsoft.web.webview2.$Version.nupkg"

if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Recurse -Force }

Write-Host "downloading Microsoft.Web.WebView2 $Version ..."
if (Get-Command curl.exe -ErrorAction SilentlyContinue) {
    & curl.exe -sSL --noproxy '*' -o $zip $url
}
if (-not (Test-Path -LiteralPath $zip) -or (Get-Item -LiteralPath $zip).Length -lt 100000) {
    Write-Host 'curl failed or returned too little, retrying with Invoke-WebRequest ...'
    Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
}
if (-not (Test-Path -LiteralPath $zip) -or (Get-Item -LiteralPath $zip).Length -lt 100000) {
    throw "download failed or looks wrong: $zip"
}

Expand-Archive -LiteralPath $zip -DestinationPath $tmp -Force

$coreDir = Join-Path $vendor 'lib\net462'
$nativeDir = Join-Path $vendor 'runtimes\win-x64\native'
New-Item -ItemType Directory -Force -Path $coreDir, $nativeDir | Out-Null

Copy-Item -LiteralPath (Join-Path $tmp 'lib\net462\Microsoft.Web.WebView2.Core.dll') -Destination $coreDir -Force
Copy-Item -LiteralPath (Join-Path $tmp 'lib\net462\Microsoft.Web.WebView2.WinForms.dll') -Destination $coreDir -Force
Copy-Item -LiteralPath (Join-Path $tmp 'runtimes\win-x64\native\WebView2Loader.dll') -Destination $nativeDir -Force

Write-Host "WebView2 SDK ready: $vendor"
