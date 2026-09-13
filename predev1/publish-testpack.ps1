#Requires -Version 5.1
<#
    0verClient - publish the test pack for the server listed in ServerIP.txt.

    This exists so that you never have to type the server address by hand.
    The address is read from ServerIP.txt, which stays the single source of truth.
    That matters because --base-url is baked into index.json / manifest.json as an
    absolute URL: publish with the wrong address and the launcher will chase a URL
    that does not exist. (That trap caught the author of this script during testing.)

    Usage:
      .\publish-testpack.ps1
      .\publish-testpack.ps1 -Port 8788
      .\publish-testpack.ps1 -SkipCheck     # do not probe the server afterwards

    ASCII only on purpose: Windows PowerShell 5.1 decodes BOM-less .ps1 files using
    the ANSI code page, so non-ASCII text would break the parser.
#>
[CmdletBinding()]
param(
    [int]$Port = 8787,
    [string]$GameId = 'testpack',
    [switch]$Local,
    [switch]$SkipCheck
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $root

try {
    # ---- read the server address (not needed in -Local mode) ----
    $serverHost = ''
    if (-not $Local) {
        $ipFile = Join-Path $root 'ServerIP.txt'
        if (-not (Test-Path $ipFile)) {
            throw @"
ServerIP.txt not found.

Create it next to this script, containing just your server's address on one line, e.g.
    203.0.113.10
or a hostname:
    game.example.com

No http:// prefix and no port - the port comes from -Port (default 8787).
See ServerIP.txt.example. This file is local configuration and is not in version control.
"@
        }

        $serverHost = (Get-Content $ipFile -Raw).Trim()
        if ([string]::IsNullOrWhiteSpace($serverHost)) { throw 'ServerIP.txt is empty.' }
        if ($serverHost -notmatch '^[0-9A-Za-z\.\-]+$') {
            throw "ServerIP.txt does not look like an IP address or hostname: '$serverHost'"
        }
    }

    # -Local: point base-url at loopback so the package can be verified on this machine
    # before it ever touches the server.
    #
    # This is not a nicety. --base-url is baked into index.json / manifest.json as
    # absolute URLs, so a package built for the server will make the launcher chase
    # the server even when you are hosting the folder locally - which looks exactly
    # like "the publishing is broken".
    $baseUrl = if ($Local) { "http://127.0.0.1:$Port" } else { "http://${serverHost}:$Port" }
    $siteDir = Join-Path $root 'build\site'

    Write-Host ''
    Write-Host '============================================================'
    Write-Host ' 0verClient - publish test pack'
    Write-Host '============================================================'
    if ($Local) {
        Write-Host '  mode      : LOCAL  (base url points at this machine)'
    }
    else {
        Write-Host "  server    : $serverHost   (from ServerIP.txt)"
    }
    Write-Host "  base url  : $baseUrl"
    Write-Host "  site dir  : $siteDir"
    Write-Host ''

    # ---- build the publisher ----
    Write-Host '==> Building tools/Publish'
    dotnet build 'tools\Publish\Publish.csproj' -c Debug -v m --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

    $publishExe = Join-Path $root 'tools\Publish\bin\Debug\net10.0-windows\Publish.exe'
    if (-not (Test-Path $publishExe)) { throw "Not found: $publishExe" }

    # ---- always rebuild the site folder from scratch ----
    # A stale index.json carrying an old address is the number one way to waste an hour.
    if (Test-Path $siteDir) {
        Write-Host "==> Wiping old site folder (so no stale address can survive)"
        Remove-Item $siteDir -Recurse -Force
    }

    Write-Host ''
    Write-Host '==> Publishing samples/TestPack'
    & $publishExe --game 'samples\TestPack' --id $GameId --name 'Server Test Pack' `
        --version '1.0.0' --base-url $baseUrl --out $siteDir `
        --summary 'Downloaded from my Tencent Cloud server' --tags 'test,server' --accent '#5CD68A'
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

    # ---- put the server-side scripts next to the site folder ----
    # Why: the server needs THREE things (two scripts and the site folder). Shipping only
    # the folder is the single easiest mistake to make, and the failure it produces
    # ("-File ... serve-site.ps1 does not exist") is not obvious. Keeping them together
    # in one folder makes it a single Ctrl+A / Ctrl+C / Ctrl+V.
    foreach ($tool in @('serve-site.ps1', 'check-server.ps1')) {
        $toolPath = Join-Path $root "tools\$tool"
        if (-not (Test-Path $toolPath)) { throw "Not found: $toolPath" }
        Copy-Item $toolPath (Join-Path $root "build\$tool") -Force
    }

    # The health check reads the public address from this file so it can also test the
    # public URL from inside the server.
    Copy-Item (Join-Path $root 'ServerIP.txt') (Join-Path $root 'build\ServerIP.txt') -Force

    # ---- print the remaining steps, in the same order as docs/SERVER.md ----
    Write-Host ''
    if ($Local) {
        Write-Host '============================================================'
        Write-Host ' NEXT - serve this folder on THIS machine'
        Write-Host '============================================================'
        Write-Host '  Option A (recommended: the very same script the server runs)'
        Write-Host ''
        Write-Host "     .\tools\serve-site.ps1 -Root build\site -Port $Port -Bind 127.0.0.1"
        Write-Host ''
        Write-Host '  Option B (the C# dev server, same job)'
        Write-Host ''
        Write-Host "     .\tools\DevServer\bin\Debug\net10.0-windows\DevServer.exe --site build\site --port $Port"
        Write-Host ''
        Write-Host '  Leave that window OPEN. That IS the content source.'
        Write-Host '  Closing it empties the game library.'
        Write-Host ''
        Write-Host '============================================================'
        Write-Host ' THEN - point the launcher at it'
        Write-Host '============================================================'
        Write-Host '  0verClient -> Settings:'
        Write-Host "     index url        : $baseUrl/index.json"
        Write-Host '     allow plain http : not needed (loopback is always allowed)'
        Write-Host '  Save -> Refresh library -> Install -> Launch'
        Write-Host ''
        Write-Host '  Once this works locally, publish again WITHOUT -Local and copy'
        Write-Host '  build\site to the server. Full steps: docs/SERVER.md'
        Write-Host '============================================================'
    }
    else {
        Write-Host '============================================================'
        Write-Host ' STEP 3 - copy EVERYTHING the server needs, in one paste'
        Write-Host '============================================================'
        Write-Host "  local folder : $(Join-Path $root 'build')"
        Write-Host '  remote path  : C:\0verclient'
        Write-Host ''
        Write-Host '  That folder now holds exactly these items:'
        Write-Host '     serve-site.ps1    <- the content server script'
        Write-Host '     check-server.ps1  <- health check (run it on the server if anything fails)'
        Write-Host '     ServerIP.txt      <- so the health check knows the public address'
        Write-Host '     site\             <- index.json + games\...'
        Write-Host ''
        Write-Host '  1) open the local folder above on this PC'
        Write-Host '  2) Ctrl+A, then Ctrl+C          (this selects BOTH items)'
        Write-Host '  3) in the RDP window: open C:\ , create a folder named 0verclient , enter it'
        Write-Host '  4) Ctrl+V'
        Write-Host ''
        Write-Host '  Check on the server with:   dir C:\0verclient'
        Write-Host '  You must end up with:'
        Write-Host '     C:\0verclient\serve-site.ps1'
        Write-Host '     C:\0verclient\check-server.ps1'
        Write-Host '     C:\0verclient\site\index.json     (index.json at the ROOT of site)'
        Write-Host ''
        Write-Host '============================================================'
        Write-Host ' STEP 4 - start the content source on the server (RDP session)'
        Write-Host '============================================================'
        Write-Host '  Copy tools\serve-site.ps1 to the server too, then open PowerShell'
        Write-Host '  AS ADMINISTRATOR and run (Windows Server blocks .ps1 by default):'
        Write-Host ''
        Write-Host "     powershell -ExecutionPolicy Bypass -File C:\0verclient\serve-site.ps1 -Root C:\0verclient\site -Port $Port -AddFirewallRule"
        Write-Host ''
        Write-Host '  Leave that window OPEN. Closing it stops the content source.'
        Write-Host ''
        Write-Host '============================================================'
        Write-Host ' STEP 5 - open the Tencent Cloud security group'
        Write-Host '============================================================'
        Write-Host '  Console -> Lightweight server -> your instance -> Firewall -> add rule'
        Write-Host "     TCP / $Port / source 0.0.0.0/0"
        Write-Host '  The machine firewall and the cloud security group are two different things.'
        Write-Host ''
        Write-Host '============================================================'
        Write-Host ' STEP 6 - verify from this PC'
        Write-Host '============================================================'
        Write-Host "  Open in a browser:  $baseUrl/index.json"
        Write-Host '  Seeing JSON means the whole chain works.'
        Write-Host ''
        Write-Host '============================================================'
        Write-Host ' STEP 7 - point the launcher at it'
        Write-Host '============================================================'
        Write-Host '  0verClient -> Settings:'
        Write-Host "     index url        : $baseUrl/index.json"
        Write-Host "     allowed hosts    : $serverHost"
        Write-Host '     allow plain http : CHECKED'
        Write-Host '  Then: Save -> Refresh library -> Install -> Launch'
        Write-Host '============================================================'
    }

    if (-not $SkipCheck) {
        Write-Host ''
        Write-Host '==> Probing the server (expected to fail until steps 3-5 are done)'
        try {
            $response = Invoke-WebRequest -Uri "$baseUrl/index.json" -UseBasicParsing -TimeoutSec 8
            Write-Host "    OK: HTTP $($response.StatusCode) - the server is already serving." -ForegroundColor Green
            Write-Host '    You can go straight to step 7.' -ForegroundColor Green
        }
        catch {
            Write-Host "    Not reachable yet: $($_.Exception.Message)" -ForegroundColor Yellow
            Write-Host '    That is expected before steps 3-5 are finished.'
        }
    }
}
finally {
    Pop-Location
}
