<#
.SYNOPSIS
  Build HoarderDB into a single hoarderdb.exe.

.DESCRIPTION
  Publishes a single-file executable. By default the exe is self-contained
  (no .NET install needed on the target machine). The IncludeAllContentForSelfExtract
  flag is required so the native RocksDB library is bundled correctly.

.PARAMETER FrameworkDependent
  Build a much smaller exe that requires .NET 9 installed on the target machine.

.PARAMETER Runtime
  Target runtime identifier (default: win-x64). e.g. win-arm64, linux-x64.

.PARAMETER OutDir
  Copy the finished exe into this folder (default: .\dist).

.EXAMPLE
  .\build-exe.ps1
  .\build-exe.ps1 -FrameworkDependent
  .\build-exe.ps1 -Runtime win-arm64
#>
[CmdletBinding()]
param(
    [switch]$FrameworkDependent,
    [string]$Runtime = "win-x64",
    [string]$OutDir  = "dist"
)

$ErrorActionPreference = "Stop"
Set-Location -Path $PSScriptRoot

# A previously-published exe can stay locked by a still-running process and break
# the copy step; stop any lingering instance first.
Get-Process -Name hoarderdb -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

$selfContained = -not $FrameworkDependent
$scFlag = if ($selfContained) { "--self-contained" } else { "--no-self-contained" }

Write-Host "Publishing hoarderdb.exe ($Runtime, $(if ($selfContained) {'self-contained'} else {'framework-dependent'}))..." -ForegroundColor Cyan

$project = Join-Path $PSScriptRoot "src/HoarderDB.Cli/HoarderDB.Cli.csproj"
dotnet publish $project -c Release -r $Runtime $scFlag `
    -p:PublishSingleFile=true `
    -p:IncludeAllContentForSelfExtract=true
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }

$publishDir = Join-Path $PSScriptRoot "src/HoarderDB.Cli/bin/Release/net9.0/$Runtime/publish"
$exeName = if ($Runtime -like "win-*") { "hoarderdb.exe" } else { "hoarderdb" }
$exePath = Join-Path $publishDir $exeName
if (-not (Test-Path $exePath)) { throw "Expected exe not found at $exePath" }

# Copy just the executable into a clean output folder.
$destDir = if ([System.IO.Path]::IsPathRooted($OutDir)) { $OutDir } else { Join-Path $PSScriptRoot $OutDir }
New-Item -ItemType Directory -Force -Path $destDir | Out-Null
$destExe = Join-Path $destDir $exeName
Copy-Item -Path $exePath -Destination $destExe -Force

$sizeMB = [math]::Round((Get-Item $destExe).Length / 1MB, 1)
Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "  exe : $destExe"
Write-Host "  size: $sizeMB MB"
Write-Host ""
Write-Host "Run it, e.g.:"
Write-Host "  $exeName import huge.txt --db D:\wordlists\main"
