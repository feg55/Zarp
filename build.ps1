# Local build: dist\Zarp.exe - a single file with zapret2 embedded.
# Releases are built by GitHub Actions (.github/workflows/build.yml) the same way.
param([string]$Version = '0.0.0')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$obj = Join-Path $root 'out'

& (Join-Path $root 'tools\fetch-zapret.ps1')

dotnet build (Join-Path $root 'src\Zarp\Zarp.csproj') -c Release -o $obj -p:Version=$Version
if ($LASTEXITCODE -ne 0) { throw 'build failed' }

New-Item -ItemType Directory -Force (Join-Path $root 'dist') | Out-Null
Copy-Item (Join-Path $obj 'Zarp.exe') (Join-Path $root 'dist\Zarp.exe') -Force
Write-Output "Done: $(Join-Path $root 'dist\Zarp.exe')"
