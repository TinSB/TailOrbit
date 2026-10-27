# Build TailOrbit.exe with the C# compiler that ships with Windows (.NET Framework 4.8, C# 5 syntax only).
# With -Out it builds somewhere else and leaves a running TailOrbit alone (release.ps1 uses that).
param([string]$Out)
$ErrorActionPreference = 'Stop'
$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Out) {
    $Out = "$here\TailOrbit.exe"
    Get-Process TailOrbit -ErrorAction SilentlyContinue | Stop-Process -Force
}
& "$fw\csc.exe" /nologo /preferreduilang:en-US /target:winexe /optimize+ /codepage:65001 "/out:$Out" "/win32icon:$here\TailOrbit.ico" `
    "/r:$fw\WPF\PresentationFramework.dll" "/r:$fw\WPF\PresentationCore.dll" "/r:$fw\WPF\WindowsBase.dll" `
    "/r:$fw\System.Xaml.dll" "/r:$fw\System.Web.Extensions.dll" "/r:$fw\System.IO.Compression.dll" `
    "/r:$fw\System.IO.Compression.FileSystem.dll" /r:Microsoft.CSharp.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll `
    "/resource:$here\TailOrbit.ico,TailOrbit.ico" "/resource:$here\icons.txt,icons.txt" `
    @(Get-ChildItem "$here\i18n\*.json" -ErrorAction SilentlyContinue | ForEach-Object { "/resource:$($_.FullName),i18n.$($_.Name)" }) `
    "$here\*.cs"
exit $LASTEXITCODE
