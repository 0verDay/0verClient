#Requires -Version 5.1
<#
    0verClient demo launcher.

      1. Builds five projects (engine / launcher / local content source / sample game / engine self-test)
      2. Starts DevServer as a local static content source (throttled so the progress bar is visible)
      3. Starts 0verClient, which reads its game library from http://127.0.0.1:8787/index.json

    Usage:
      .\run-demo.ps1                  normal demo
      .\run-demo.ps1 -SmokeTest       run the headless engine self-test first
      .\run-demo.ps1 -DropAfter       server cuts the first connection mid-transfer (shows resume)
      .\run-demo.ps1 -NoThrottle      no rate limit
      .\run-demo.ps1 -SkipBuild       skip the build step

    TWO RULES learned the hard way -- keep both when editing this file:

    1. Keep this file pure ASCII.
       Windows PowerShell 5.1 reads a BOM-less .ps1 using the ANSI code page (GBK on
       zh-CN Windows). UTF-8 Chinese then turns into mojibake and breaks the parser.

    2. Never reuse a parameter name as a local variable name.
       `[switch]$SmokeTest` makes $SmokeTest a type-constrained SwitchParameter variable,
       so a later `$smokeTest = <path>` throws
       "Cannot convert ... System.String ... to SwitchParameter".
       That is why every path below is named *Exe / *Root instead of reusing a param name.
#>
[CmdletBinding()]
param(
    [int]$Port = 8787,
    [int]$ThrottleKbps = 512,
    [switch]$DropAfter,
    [switch]$NoThrottle,
    [switch]$SmokeTest,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $root

$server = $null
$buildConfig = 'Debug'
$targetFramework = 'net10.0-windows'
$outDir = "bin\$buildConfig\$targetFramework"

try {
    if (-not $SkipBuild) {
        Write-Host '==> Building' -ForegroundColor Cyan
        $projects = @(
            'src\0verClient.Core\0verClient.Core.csproj',
            'samples\DemoGame\DemoGame.csproj',
            'tools\DevServer\DevServer.csproj',
            'tools\SmokeTest\SmokeTest.csproj',
            'tools\Publish\Publish.csproj',
            'src\0verClient.App\0verClient.App.csproj'
        )
        foreach ($projectPath in $projects) {
            dotnet build $projectPath -c $buildConfig -v m --nologo
            if ($LASTEXITCODE -ne 0) { throw "Build failed: $projectPath" }
        }
    }

    $devServerExe = Join-Path $root "tools\DevServer\$outDir\DevServer.exe"
    $smokeExe     = Join-Path $root "tools\SmokeTest\$outDir\SmokeTest.exe"
    $gameContentRoot = Join-Path $root "samples\DemoGame\$outDir"
    $launcherExe  = Join-Path $root "src\0verClient.App\$outDir\0verClient.exe"

    foreach ($requiredPath in @($devServerExe, $launcherExe)) {
        if (-not (Test-Path $requiredPath)) { throw "Not found: $requiredPath -- run again without -SkipBuild." }
    }
    if (-not (Test-Path $gameContentRoot)) { throw "Not found: $gameContentRoot (sample game output)." }

    $effectiveThrottle = if ($NoThrottle) { 0 } else { $ThrottleKbps }
    $serverArgs = @('--root', $gameContentRoot, '--port', $Port, '--throttle-kbps', $effectiveThrottle)
    if ($DropAfter) { $serverArgs += @('--drop-after', 262144) }

    Write-Host "==> Starting local content source on http://127.0.0.1:$Port/index.json" -ForegroundColor Cyan
    $server = Start-Process -FilePath $devServerExe -ArgumentList $serverArgs -PassThru -WindowStyle Minimized

    # Wait until the port actually accepts connections. A bare Start-Sleep is a guess:
    # if the port is already taken or the process died quietly, the launcher would open
    # with an empty library and no obvious reason.
    $listening = $false
    for ($attempt = 1; $attempt -le 15; $attempt++) {
        if ($server.HasExited) { throw "Content source exited with code $($server.ExitCode). Run DevServer alone to see why." }
        try {
            $probe = [System.Net.Sockets.TcpClient]::new()
            $probe.Connect('127.0.0.1', $Port)
            $probe.Close()
            $listening = $true
            break
        }
        catch {
            Start-Sleep -Milliseconds 400
        }
    }
    if (-not $listening) {
        throw "Nothing is listening on 127.0.0.1:$Port after 6 seconds. The port may already be in use by another program."
    }
    Write-Host "    content source is up. KEEP THAT WINDOW OPEN - closing it empties the game library." -ForegroundColor DarkGray

    if ($SmokeTest) {
        if (-not (Test-Path $smokeExe)) { throw "Not found: $smokeExe" }
        Write-Host '==> Engine self-test' -ForegroundColor Cyan
        & $smokeExe --index "http://127.0.0.1:$Port/index.json" --fresh
        if ($LASTEXITCODE -ne 0) { throw 'Engine self-test failed.' }
    }

    Write-Host '==> Starting 0verClient' -ForegroundColor Cyan
    Write-Host '    Click INSTALL on the game card, watch the progress bar, then click LAUNCH.' -ForegroundColor DarkGray
    & $launcherExe
}
finally {
    if ($server -and -not $server.HasExited) {
        Write-Host '==> Stopping content source' -ForegroundColor DarkGray
        Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
    }
    Pop-Location
}
