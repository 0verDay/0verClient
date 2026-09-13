#Requires -Version 5.1
<#
    0verClient - server-side health check.

    Run this ON THE SERVER when the launcher or a browser cannot reach
    http://<public-ip>:<port>/index.json . It checks all six layers in order and
    tells you which one is broken, instead of leaving you to guess.

    Usage:
      powershell -ExecutionPolicy Bypass -File C:\0verclient\check-server.ps1
      powershell -ExecutionPolicy Bypass -File C:\0verclient\check-server.ps1 -Port 8788
      powershell -ExecutionPolicy Bypass -File C:\0verclient\check-server.ps1 -PublicIp 1.2.3.4

    Exit code 0 = everything on this machine is fine (the problem is the cloud
    security group). Exit code 1 = something on this machine needs fixing.

    ASCII only on purpose: Windows PowerShell 5.1 decodes BOM-less .ps1 files using
    the ANSI code page, and non-ASCII text breaks the parser.
#>
[CmdletBinding()]
param(
    [string]$Root = 'C:\0verclient\site',
    [int]$Port = 8787,
    [string]$PublicIp = ''
)

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($PublicIp)) {
    $ipFile = Join-Path $scriptDir 'ServerIP.txt'
    if (Test-Path $ipFile) { $PublicIp = (Get-Content $ipFile -Raw).Trim() }
}

$failures = New-Object System.Collections.ArrayList

function Check([string]$name, [scriptblock]$test) {
    try {
        if (& $test) {
            Write-Host "  [OK]   $name" -ForegroundColor Green
            return $true
        }
        Write-Host "  [FAIL] $name" -ForegroundColor Red
        [void]$failures.Add($name)
        return $false
    }
    catch {
        Write-Host "  [FAIL] $name" -ForegroundColor Red
        Write-Host "         $($_.Exception.Message)" -ForegroundColor DarkGray
        [void]$failures.Add($name)
        return $false
    }
}

function Get-IndexJson {
    $r = Invoke-WebRequest -Uri "http://127.0.0.1:$Port/index.json" -UseBasicParsing -TimeoutSec 8
    return ($r.Content | ConvertFrom-Json)
}

Write-Host ''
Write-Host '============================================================'
Write-Host ' 0verClient - server health check'
Write-Host '============================================================'
Write-Host "  site root : $Root"
Write-Host "  port      : $Port"
if ($PublicIp) { Write-Host "  public ip : $PublicIp" }
Write-Host ''

Write-Host '[1/6] files on disk'
[void](Check "index.json exists at $Root" {
    Test-Path (Join-Path $Root 'index.json') -PathType Leaf
})
[void](Check "games folder exists under $Root" {
    Test-Path (Join-Path $Root 'games') -PathType Container
})

Write-Host ''
Write-Host '[2/6] is anything listening on the port'
[void](Check "TCP 127.0.0.1:$Port accepts connections" {
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $client.Connect('127.0.0.1', $Port)
        return $true
    }
    catch {
        return $false
    }
    finally {
        $client.Close()
    }
})

Write-Host ''
Write-Host '[3/6] http from this machine'
[void](Check "GET http://127.0.0.1:$Port/index.json returns 200" {
    $r = Invoke-WebRequest -Uri "http://127.0.0.1:$Port/index.json" -UseBasicParsing -TimeoutSec 8
    return ($r.StatusCode -eq 200)
})

Write-Host ''
Write-Host '[4/6] index contents'
[void](Check "index.json parses and lists at least one game" {
    $index = Get-IndexJson
    if (-not $index.games) { return $false }
    $ids = @($index.games | ForEach-Object { $_.id })
    Write-Host "         games: $($ids.Count) -> $($ids -join ', ')" -ForegroundColor DarkGray
    return ($ids.Count -gt 0)
})

Write-Host ''
Write-Host '[5/6] are the manifest urls built for this server (not for a local machine)'
[void](Check "no manifest url points at 127.0.0.1 / localhost" {
    $index = Get-IndexJson
    $bad = New-Object System.Collections.ArrayList
    foreach ($game in $index.games) {
        foreach ($channel in $game.channels.PSObject.Properties) {
            $url = $channel.Value.manifestUrl
            if ($url -match '127\.0\.0\.1|localhost') { [void]$bad.Add("$($game.id) -> $url") }
        }
    }
    if ($bad.Count -gt 0) {
        foreach ($item in $bad) { Write-Host "         $item" -ForegroundColor Yellow }
        return $false
    }
    return $true
})

Write-Host ''
Write-Host '[6/6] firewall and public reachability'
try {
    $rules = Get-NetFirewallRule -ErrorAction Stop | Where-Object { $_.DisplayName -like '*0verClient*' }
    if ($rules) {
        foreach ($rule in $rules) {
            Write-Host "  [info] $($rule.DisplayName)  enabled=$($rule.Enabled)  action=$($rule.Action)" -ForegroundColor DarkGray
        }
    }
    else {
        Write-Host '  [warn] no firewall rule named *0verClient*' -ForegroundColor Yellow
        Write-Host '         add one: New-NetFirewallRule -DisplayName "0verClient content" -Direction Inbound -Protocol TCP -LocalPort 8787 -Action Allow' -ForegroundColor DarkGray
    }
}
catch {
    Write-Host "  [info] cannot query the firewall here: $($_.Exception.Message)" -ForegroundColor DarkGray
}

if ($PublicIp) {
    [void](Check "GET http://${PublicIp}:$Port/index.json from this machine" {
        $r = Invoke-WebRequest -Uri "http://${PublicIp}:$Port/index.json" -UseBasicParsing -TimeoutSec 10
        return ($r.StatusCode -eq 200)
    })
}
else {
    Write-Host '  [info] no public IP known - pass -PublicIp to also test the public address' -ForegroundColor DarkGray
}

Write-Host ''
Write-Host '============================================================'
if ($failures.Count -eq 0) {
    Write-Host ' EVERYTHING ON THIS MACHINE IS FINE' -ForegroundColor Green
    Write-Host '============================================================'
    Write-Host ' If your PC still cannot reach the address, the problem is outside this'
    Write-Host ' machine - almost always the Tencent Cloud security group:'
    Write-Host '   Console -> Lightweight server -> your instance -> Firewall'
    Write-Host "   add rule: TCP / $Port / source 0.0.0.0/0"
    exit 0
}

Write-Host " FAILED CHECKS: $($failures.Count)" -ForegroundColor Red
Write-Host '============================================================'
Write-Host ' What each failure means:'
Write-Host '   index.json missing      -> wrong folder level on the server (or site was never copied)'
Write-Host '   games folder missing    -> same as above'
Write-Host '   port not listening      -> the content source is not running. Start it:'
Write-Host "                              powershell -ExecutionPolicy Bypass -File `"$scriptDir\serve-site.ps1`" -Root $Root -Port $Port -AddFirewallRule"
Write-Host '   http 127.0.0.1 fails    -> content source died, or -Root points at the wrong folder'
Write-Host '   index unreadable        -> the files on the server are incomplete or corrupted'
Write-Host '   manifest urls 127.0.0.1 -> you copied a package built with -Local. On your PC run'
Write-Host '                              .\publish-testpack.ps1   (WITHOUT -Local) and copy build\site again'
Write-Host '   public IP fails         -> Tencent Cloud security group, or the cloud firewall'
Write-Host ''
Write-Host ' You can paste the output of this script back to whoever is helping you.'
exit 1
