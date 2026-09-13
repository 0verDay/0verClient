#Requires -Version 5.1
<#
    0verClient - remove everything that must not be committed.

    Only build output is deleted. Source, docs, samples and config are never touched.

    Usage:
      .\clean.ps1                        delete bin / obj / dist / build
      .\clean.ps1 -WhatIf                show what would be deleted, delete nothing
      .\clean.ps1 -IncludeToolCache      also delete .dotnet / .nuget / .tmp if present

    ASCII only on purpose: Windows PowerShell 5.1 decodes BOM-less .ps1 files using
    the ANSI code page, and non-ASCII text breaks the parser.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch]$IncludeToolCache
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

# Collect precisely instead of globbing by name: a folder called "bin" somewhere deep
# inside a published site must not be mistaken for build output.
$targets = New-Object System.Collections.ArrayList

foreach ($project in Get-ChildItem $root -Recurse -Filter *.csproj -File -ErrorAction SilentlyContinue) {
    $projectDir = Split-Path $project.FullName -Parent
    foreach ($name in @('bin', 'obj')) {
        $path = Join-Path $projectDir $name
        if (Test-Path $path) { [void]$targets.Add($path) }
    }
}

$topLevel = @('dist', 'build', 'artifacts', '.build')
if ($IncludeToolCache) { $topLevel += @('.dotnet', '.nuget', '.tmp') }

foreach ($name in $topLevel) {
    $path = Join-Path $root $name
    if (Test-Path $path) { [void]$targets.Add($path) }
}

# Drop entries nested inside another entry so we never delete the same thing twice.
$final = @()
foreach ($path in ($targets | Sort-Object -Unique)) {
    $nested = $false
    foreach ($other in $targets) {
        if ($other -ne $path -and $path.StartsWith($other + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
            $nested = $true
            break
        }
    }
    if (-not $nested) { $final += $path }
}

if ($final.Count -eq 0) {
    Write-Host 'Nothing to clean.'
    exit 0
}

Write-Host ''
Write-Host '0verClient - clean build output'
Write-Host ''

$total = 0

foreach ($path in $final) {
    $size = (Get-ChildItem $path -Recurse -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum
    $total += $size
    $relative = $path.Substring($root.Length + 1)

    if ($PSCmdlet.ShouldProcess($relative, 'Remove')) {
        Remove-Item $path -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host ("  removed        {0,-52} {1,8:0.0} MB" -f $relative, ($size / 1MB))
    }
    else {
        Write-Host ("  would remove   {0,-52} {1,8:0.0} MB" -f $relative, ($size / 1MB))
    }
}

Write-Host ''
Write-Host ("total {0:0.0} MB" -f ($total / 1MB))
Write-Host ''
Write-Host 'Source, docs, samples and config were not touched.'
Write-Host 'Note: .probe.txt is a leftover probe file, not build output - delete it by hand.'
