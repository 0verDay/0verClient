#Requires -Version 5.1
<#
    0verClient - build distributables.

    Produces two things that together are the whole product:

      dist\server\   0verClient.Server.exe   serves a published site over HTTP (Range supported)
      dist\client\   0verClient.exe          the launcher players run

    Three packaging modes, pick by how much you want to depend on the target machine:

      (default)        framework-dependent folder
                       ~290 KB. The target machine needs the .NET 10 runtime.

      -Aot             NativeAOT: native single exe, roughly 5-10 MB, NO runtime at all
                       and instant startup. Best option for the SERVER.
                       WPF (the client) does not support NativeAOT, so this mode always
                       requires -Target server. Needs the MSVC toolchain (Visual Studio)
                       and downloads ILCompiler once.

      -Trimmed         self-contained + trimmed + single file, roughly 12-20 MB.
                       Same "no runtime needed" benefit, a bit bigger than AOT.

      -SelfContained   includes the runtime, as a FOLDER (~77 MB server / ~171 MB client).
                       Copy anywhere, run. For the client prefer -SingleFile below:
                       same zero-dependency result at 59 MB instead of 171 MB.

      -SingleFile      like -SelfContained but packed into ONE compressed .exe
                       (client: 59 MB, extracts ~8 MB of native libraries on first run).
                       Use this for the client - WPF cannot be AOT'd or trimmed
                       (NETSDK1168), so this is as small as a zero-dependency client gets.

      -Portable        NO internet needed. Stays framework-dependent but copies the
                       runtime that is already installed on THIS machine next to the
                       exe, plus a run-*.cmd that points DOTNET_ROOT at it.

    Usage:
      .\build-dist.ps1 -Target server -Aot        recommended for the server
      .\build-dist.ps1 -Portable
      .\build-dist.ps1 -SelfContained -SingleFile
      .\build-dist.ps1

    ASCII only on purpose: Windows PowerShell 5.1 decodes BOM-less .ps1 files using
    the ANSI code page, and non-ASCII text breaks the parser.

    NOTE: the helper functions below must stay ABOVE the main body. PowerShell resolves
    command names at execution time, so a function defined after its call site is a
    CommandNotFoundException.
#>
[CmdletBinding()]
param(
    [ValidateSet('both', 'server', 'client')][string]$Target = 'both',
    [string]$Configuration = 'Release',
    [switch]$SelfContained,
    [switch]$SingleFile,
    [switch]$Portable,
    [switch]$Aot,
    [switch]$Trimmed,
    [switch]$KeepSymbols,
    [int]$ServerUrlPort = 8787,
    [string]$OutRoot = 'dist'
)

$ErrorActionPreference = 'Stop'

function Format-Size([long]$bytes) {
    if ($bytes -ge 1GB) { return ('{0:0.##} GB' -f ($bytes / 1GB)) }
    if ($bytes -ge 1MB) { return ('{0:0.##} MB' -f ($bytes / 1MB)) }
    if ($bytes -ge 1KB) { return ('{0:0.##} KB' -f ($bytes / 1KB)) }
    return "$bytes B"
}

function Copy-LocalRuntime([string]$OutDir, [bool]$NeedsDesktop) {
    $dotnetExe = (Get-Command dotnet -ErrorAction Stop).Source
    $dotnetRoot = Split-Path -Parent $dotnetExe
    $sharedRoot = Join-Path $dotnetRoot 'shared'
    if (-not (Test-Path $sharedRoot)) { throw "cannot find the installed runtime under $sharedRoot" }

    $frameworks = @('Microsoft.NETCore.App')
    if ($NeedsDesktop) { $frameworks += 'Microsoft.WindowsDesktop.App' }

    foreach ($framework in $frameworks) {
        $frameworkRoot = Join-Path $sharedRoot $framework
        if (-not (Test-Path $frameworkRoot)) { throw "runtime not installed: $frameworkRoot" }

        $version = Get-ChildItem $frameworkRoot -Directory |
            Where-Object { $_.Name -like '10.*' } |
            Sort-Object Name -Descending |
            Select-Object -First 1

        if (-not $version) { throw "no 10.x runtime found under $frameworkRoot" }

        $destination = Join-Path $OutDir "runtime\shared\$framework\$($version.Name)"
        New-Item -ItemType Directory -Force -Path $destination | Out-Null
        Copy-Item (Join-Path $version.FullName '*') $destination -Recurse -Force
        Write-Host "    bundled $framework $($version.Name)"
    }

    # The .NET HOST itself. hostfxr.dll lives in host\fxr\<version>, NOT under shared\.
    # Copying only shared\ produces a bundle that looks complete but fails at startup with
    # "You must install .NET to run this application / .NET location: Not found" --
    # while every runtime file is sitting right there. Measured, not guessed.
    $fxrRoot = Join-Path $dotnetRoot 'host\fxr'
    if (-not (Test-Path $fxrRoot)) { throw "cannot find the .NET host under $fxrRoot" }

    $fxrVersion = Get-ChildItem $fxrRoot -Directory |
        Where-Object { $_.Name -like '10.*' } |
        Sort-Object Name -Descending |
        Select-Object -First 1

    if (-not $fxrVersion) { throw "no 10.x hostfxr found under $fxrRoot" }

    $fxrDestination = Join-Path $OutDir "runtime\host\fxr\$($fxrVersion.Name)"
    New-Item -ItemType Directory -Force -Path $fxrDestination | Out-Null
    Copy-Item (Join-Path $fxrVersion.FullName '*') $fxrDestination -Recurse -Force
    Write-Host "    bundled host\fxr $($fxrVersion.Name)"
}

function Write-RunCmd([string]$OutDir, [string]$ExeName, [string]$RunCmd, [bool]$UsesBundledRuntime, [string]$DefaultArgs = '') {
    $lines = @('@echo off')
    if ($UsesBundledRuntime) {
        # DOTNET_ROOT makes the apphost look for the runtime next to the exe instead of
        # relying on a machine-wide installation.
        $lines += 'set "DOTNET_ROOT=%~dp0runtime"'
    }

    $call = "`"%~dp0$ExeName.exe`""
    if ($DefaultArgs) { $call += " $DefaultArgs" }
    $lines += "$call %*"
    $lines += 'if errorlevel 1 pause'

    $path = Join-Path $OutDir $RunCmd
    [System.IO.File]::WriteAllText($path, (($lines -join "`r`n") + "`r`n"), [System.Text.Encoding]::ASCII)
}

function Write-ClientPreset([string]$OutDir, [string]$ServerUrl, [string]$ServerHost) {
    if (-not $ServerUrl) { return }

    $preset = [ordered]@{
        indexUrl          = "$ServerUrl/index.json"
        channel           = 'latest'
        allowedHosts      = $ServerHost
        allowInsecureHttp = $true
    }

    $json = $preset | ConvertTo-Json
    $path = Join-Path $OutDir 'launcher.json'
    [System.IO.File]::WriteAllText($path, $json, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "    wrote launcher.json preset -> $ServerUrl/index.json"
}

# ------------------------------------------------------------------ main

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $root

try {
    if ($SingleFile -and -not $SelfContained) {
        throw '-SingleFile requires -SelfContained (a single file only makes sense with the runtime inside).'
    }
    if ($Portable -and ($SelfContained -or $SingleFile)) {
        throw '-Portable cannot be combined with -SelfContained/-SingleFile. Pick one packaging mode.'
    }
    if ($Aot -and ($SelfContained -or $SingleFile -or $Trimmed -or $Portable)) {
        throw '-Aot is a packaging mode of its own. Do not combine it with the others.'
    }
    if ($Trimmed -and ($SelfContained -or $SingleFile -or $Portable)) {
        throw '-Trimmed is a packaging mode of its own. Do not combine it with the others.'
    }
    if ($Aot -and $Target -ne 'server') {
        throw '-Aot only applies to the server. WPF (the client) does not support NativeAOT. Use -Target server -Aot, and build the client with -SelfContained or -Portable.'
    }

    $rid = 'win-x64'

    $serverUrl = $null
    $serverHost = $null
    $ipFile = Join-Path $root 'ServerIP.txt'
    if (Test-Path $ipFile) {
        $serverHost = (Get-Content $ipFile -Raw).Trim()
        if ($serverHost) { $serverUrl = "http://${serverHost}:$ServerUrlPort" }
    }

    Write-Host ''
    Write-Host '============================================================'
    Write-Host ' 0verClient - build distributables'
    Write-Host '============================================================'
    Write-Host "  target      : $Target"
    Write-Host "  mode        : $(if ($Aot) { 'NativeAOT (native single exe, no runtime shipped)' } elseif ($Trimmed) { 'self-contained, trimmed + single file' } elseif ($SingleFile) { 'self-contained, single file, compressed' } elseif ($SelfContained) { 'self-contained folder' } elseif ($Portable) { 'portable (bundled runtime, offline)' } else { 'framework-dependent' })"
    if ($serverUrl) { Write-Host "  server url  : $serverUrl   (from ServerIP.txt)" }
    else { Write-Host '  server url  : (ServerIP.txt not found - client preset keeps the built-in default)' }
    Write-Host ''

    $outRootFull = Join-Path $root $OutRoot
    New-Item -ItemType Directory -Force -Path $outRootFull | Out-Null

    $targets = @()
    if ($Target -in @('both', 'server')) {
        $targets += [pscustomobject]@{
            Name         = 'server'
            Project      = 'src\0verClient.Server\0verClient.Server.csproj'
            ExeName      = '0verClient.Server'
            RunCmd       = 'run-server.cmd'
            NeedsDesktop = $false
            DefaultArgs  = '--root "%~dp0site" --port 8787 --add-firewall-rule'
        }
    }
    if ($Target -in @('both', 'client')) {
        $targets += [pscustomobject]@{
            Name         = 'client'
            Project      = 'src\0verClient.App\0verClient.App.csproj'
            ExeName      = '0verClient'
            RunCmd       = 'run-client.cmd'
            NeedsDesktop = $true
            DefaultArgs  = ''
        }
    }

    foreach ($t in $targets) {
        if (-not (Test-Path $t.Project)) { throw "Not found: $($t.Project)" }

        $outDir = Join-Path $outRootFull $t.Name
        if (Test-Path $outDir) {
            # Keep a "site" folder if one is parked here. run-server.cmd defaults to
            # --root "%~dp0site", so people naturally put their content next to the exe;
            # wiping it on every build would silently delete gigabytes of game files.
            Get-ChildItem $outDir -Force | Where-Object { $_.Name -ne 'site' } | Remove-Item -Recurse -Force
        }

        Write-Host "==> publishing $($t.Name) -> $(Join-Path $OutRoot $t.Name)"
        if ($Aot -or $Trimmed -or $SelfContained) {
            Write-Host '    (the first run of this mode downloads a package from nuget.org)'
        }

        $publishArgs = @('publish', $t.Project, '-c', $Configuration, '-o', $outDir, '-v', 'm', '--nologo')

        if ($Aot) {
            # NativeAOT: IL is compiled to native code up front, so there is no runtime to
            # ship at all. Needs the MSVC toolchain plus, once, the ILCompiler package.
            $publishArgs += @('-r', $rid, '-p:PublishAot=true')
        }
        elseif ($Trimmed) {
            $publishArgs += @('-r', $rid, '--self-contained', 'true', '-p:PublishTrimmed=true', '-p:PublishSingleFile=true')
        }
        elseif ($SelfContained) {
            $publishArgs += @('-r', $rid, '--self-contained', 'true')
            if ($SingleFile) {
                # Compression matters a lot for the WPF client: the same package goes from
                # ~160 MB to 59 MB (measured). Only native libraries are extracted on first
                # run (~8 MB), so the on-disk footprint stays around 67 MB rather than 171.
                $publishArgs += @(
                    '-p:PublishSingleFile=true',
                    '-p:IncludeNativeLibrariesForSelfExtract=true',
                    '-p:EnableCompressionInSingleFile=true'
                )
            }
        }

        & dotnet @publishArgs
        if ($LASTEXITCODE -ne 0) { throw "publish failed for $($t.Name)" }

        if ($Portable) {
            Copy-LocalRuntime -OutDir $outDir -NeedsDesktop $t.NeedsDesktop
            Write-RunCmd -OutDir $outDir -ExeName $t.ExeName -RunCmd $t.RunCmd -UsesBundledRuntime $true -DefaultArgs $t.DefaultArgs
            Write-Host '    bundled runtime copied (offline: nothing to install on the target)'
        }
        else {
            Write-RunCmd -OutDir $outDir -ExeName $t.ExeName -RunCmd $t.RunCmd -UsesBundledRuntime $false -DefaultArgs $t.DefaultArgs
        }

        if ($t.Name -eq 'client') {
            Write-ClientPreset -OutDir $outDir -ServerUrl $serverUrl -ServerHost $serverHost
        }

        # Debug symbols are useless on a target machine, and for NativeAOT the .pdb can be
        # several times larger than the program itself (measured: 9.7 MB pdb vs 2.1 MB exe,
        # so the "11.79 MB" package was 83% symbols). Strip them unless asked to keep them.
        if (-not $KeepSymbols) {
            $pdbs = @(Get-ChildItem $outDir -Recurse -Filter *.pdb -File -ErrorAction SilentlyContinue)
            if ($pdbs.Count -gt 0) {
                $pdbSize = ($pdbs | Measure-Object Length -Sum).Sum
                $pdbs | Remove-Item -Force -ErrorAction SilentlyContinue
                Write-Host "    stripped $(Format-Size $pdbSize) of debug symbols (-KeepSymbols keeps them)"
            }
        }

        $exePath = Join-Path $outDir "$($t.ExeName).exe"
        if (-not (Test-Path $exePath)) { throw "expected output missing: $exePath" }

        $size = (Get-ChildItem $outDir -Recurse -File | Measure-Object Length -Sum).Sum
        Write-Host "    -> $(Format-Size $size) in $(Join-Path $OutRoot $t.Name)"
        Write-Host ''
    }

    Write-Host '============================================================'
    Write-Host ' NEXT'
    Write-Host '============================================================'
    Write-Host ''
    Write-Host ' SERVER - copy dist\server AND the site folder side by side on the server:'
    Write-Host '     C:\0verclient\0verClient.Server.exe   (plus its dlls and run-server.cmd)'
    Write-Host '     C:\0verclient\site\index.json'
    Write-Host '   then just double-click run-server.cmd (it already passes'
    Write-Host '   --root "%~dp0site" --port 8787 --add-firewall-rule), or run it yourself:'
    Write-Host '     0verClient.Server.exe --root C:\0verclient\site --port 8787 --add-firewall-rule'
    Write-Host ''
    Write-Host '   Paste the folder into an RDP session with Ctrl+A / Ctrl+C / Ctrl+V.'
    Write-Host '   The site folder itself comes from publish-testpack.ps1 (or Publish.exe).'
    Write-Host ''
    Write-Host ' CLIENT - copy dist\client to whoever should play:'
    Write-Host '     dist\client\0verClient.exe'
    if ($serverUrl) {
        Write-Host "   launcher.json next to the exe already points at $serverUrl"
    }
    Write-Host '   Settings live in %LOCALAPPDATA%\0verClient\settings.json'
    Write-Host ''
    Write-Host ' .pdb files are stripped by default (they can dwarf the program). Add'
    Write-Host ' -KeepSymbols if you need them for crash analysis.'
    Write-Host ''
    Write-Host ' See docs\index.html for the full walkthrough.'
    Write-Host '============================================================'
}
finally {
    Pop-Location
}
