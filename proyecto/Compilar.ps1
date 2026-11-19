[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '0.2.0',
    [string]$Destino = (Join-Path $PSScriptRoot 'dist')
)
$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Se requiere .NET Framework 4.8 en Windows.' }
$Destino = [IO.Path]::GetFullPath($Destino)
[IO.Directory]::CreateDirectory($Destino) | Out-Null
$versionFile = Join-Path ([IO.Path]::GetTempPath()) ('EmpireBay-' + [guid]::NewGuid().ToString('N') + '.cs')
try {
    $assembly = @"
using System.Reflection;
[assembly: AssemblyTitle("Empire Bay Roleplay")]
[assembly: AssemblyProduct("MafiaRol Launcher")]
[assembly: AssemblyCompany("Empire Bay Roleplay")]
[assembly: AssemblyVersion("$Version.0")]
[assembly: AssemblyFileVersion("$Version.0")]
"@
    [IO.File]::WriteAllText($versionFile, $assembly)
    $refs = @('System','System.Core','System.Xaml','System.Net.Http','System.Web.Extensions','System.IO.Compression','System.IO.Compression.FileSystem','Microsoft.CSharp','WPF\WindowsBase','WPF\PresentationCore','WPF\PresentationFramework')
    $arguments = @('/nologo','/target:winexe','/platform:anycpu','/optimize+','/codepage:65001',('/out:' + (Join-Path $Destino 'MafiaRolLauncher.exe')))
    $arguments += $refs | ForEach-Object { '/reference:' + (Join-Path $framework ($_ + '.dll')) }
    $icon = Join-Path $PSScriptRoot 'art\EmpireBay.ico'
    $arguments += '/win32icon:' + $icon
    $arguments += '/win32manifest:' + (Join-Path $PSScriptRoot 'src\app.manifest')
    $arguments += '/resource:' + $icon + ',EmpireBay.ico'
    foreach ($asset in @('src\Window.xaml','art\logo.png','servidor.json')) {
        $arguments += '/resource:' + (Join-Path $PSScriptRoot $asset) + ',' + [IO.Path]::GetFileName($asset)
    }
    foreach ($scene in (Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'art') -Filter '*.jpg' | Sort-Object Name)) {
        $arguments += '/resource:' + $scene.FullName + ',' + $scene.Name
    }
    $arguments += @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | Sort-Object Name | ForEach-Object { $_.FullName })
    $arguments += $versionFile
    & $compiler @arguments
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo compilar el launcher.' }
    $file = Join-Path $Destino 'MafiaRolLauncher.exe'
    $probe = Start-Process -FilePath $file -ArgumentList '--comprobar' -PassThru -Wait -WindowStyle Hidden
    if ($probe.ExitCode -ne 0) { throw 'Falló la comprobación de arranque de la versión compilada.' }
    Write-Output $file
} finally {
    if (Test-Path -LiteralPath $versionFile) { Remove-Item -LiteralPath $versionFile }
}
