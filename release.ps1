# Package a release: builds dist\TailOrbit-Setup-<version>.exe plus its SHA-256, without touching a running TailOrbit.
# The installer is TailOrbit.exe under another name: with "Setup" in the file name, double-clicking it installs (see Setup.cs).
# Publish with:  gh release create v<version> dist\TailOrbit-Setup-<version>.exe dist\TailOrbit-Setup-<version>.exe.sha256
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$version = [regex]::Match((Get-Content "$here\TailOrbit.cs" -Raw), 'AssemblyVersion\("(\d+\.\d+\.\d+)').Groups[1].Value
$dist = Join-Path $here 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
$exe = Join-Path $dist 'TailOrbit.exe'
& "$here\build.ps1" -Out $exe
if ($LASTEXITCODE) { exit $LASTEXITCODE }
$setup = Join-Path $dist "TailOrbit-Setup-$version.exe"
Move-Item $exe $setup -Force
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  TailOrbit-Setup-$version.exe" | Set-Content "$setup.sha256" -Encoding ascii
$setup
"SHA-256 $hash"
