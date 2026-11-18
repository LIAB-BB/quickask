param([switch]$SkipIcon, [string]$Name = 'QuickAsk')
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $root
try {
    $fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
    $csc = Join-Path $fw 'csc.exe'

    New-Item -ItemType Directory -Force -Path 'dist' | Out-Null

    if (-not $SkipIcon -and -not (Test-Path -LiteralPath 'assets\app.ico')) {
        & 'tools\make-icon.ps1' | Out-Null
    }

    $refs = @(
        (Join-Path $fw 'System.dll'),
        (Join-Path $fw 'System.Core.dll'),
        (Join-Path $fw 'System.Windows.Forms.dll'),
        (Join-Path $fw 'System.Drawing.dll'),
        (Join-Path $fw 'System.Net.Http.dll'),
        (Join-Path $fw 'System.Xml.dll'),
        (Join-Path $fw 'System.Xml.Linq.dll'),
        (Join-Path $fw 'System.Web.Extensions.dll')
    ) | ForEach-Object { '/r:' + $_ }
    $refs += '/r:vendor\webview2\lib\net462\Microsoft.Web.WebView2.Core.dll'
    $refs += '/r:vendor\webview2\lib\net462\Microsoft.Web.WebView2.WinForms.dll'

    $sources = Get-ChildItem -LiteralPath 'src' -Filter '*.cs' |
        Sort-Object Name |
        ForEach-Object { 'src\' + $_.Name }

    $cscArgs = @(
        '/nologo', '/target:winexe', '/platform:x64', '/optimize+',
        ('/out:dist\' + $Name + '.exe'),
        '/win32manifest:app.manifest',
        '/resource:ui.html,ui.html'
    ) + $refs + $sources
    if (Test-Path -LiteralPath 'assets\app.ico') { $cscArgs += '/win32icon:assets\app.ico' }

    & $csc @cscArgs
    if ($LASTEXITCODE -ne 0) { throw "csc failed with exit code $LASTEXITCODE" }

    # Console twin for --selftest, so a shell can wait for it and read its exit code.
    $consoleArgs = @(
        '/nologo', '/target:exe', '/platform:x64', '/optimize+',
        ('/out:dist\' + $Name + '.Selftest.exe'),
        '/resource:ui.html,ui.html'
    ) + $refs + $sources
    & $csc @consoleArgs
    if ($LASTEXITCODE -ne 0) { throw "console csc failed with exit code $LASTEXITCODE" }

    $dlls = @(
        'vendor\webview2\lib\net462\Microsoft.Web.WebView2.Core.dll',
        'vendor\webview2\lib\net462\Microsoft.Web.WebView2.WinForms.dll',
        'vendor\webview2\runtimes\win-x64\native\WebView2Loader.dll'
    )
    foreach ($dll in $dlls) {
        $target = Join-Path 'dist' (Split-Path $dll -Leaf)
        if (-not (Test-Path -LiteralPath $target)) { Copy-Item -LiteralPath $dll -Destination 'dist' -Force }
    }

    Write-Output "built: $root\dist\$Name.exe"
}
finally {
    Pop-Location
}
