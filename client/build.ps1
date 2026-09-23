param([switch]$Check, [switch]$CompileOnly, [string]$Python = "")
$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
$output = Join-Path $project 'artifacts\client-windows-amd64'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$references = @('System.dll','System.Core.dll','System.Management.dll','System.Security.dll','System.Web.Extensions.dll','System.ServiceProcess.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Xaml.dll','WPF\WindowsBase.dll','WPF\PresentationFramework.dll','WPF\PresentationCore.dll')
$arguments = @('/nologo','/target:winexe','/platform:x64','/optimize+',('/out:' + (Join-Path $output 'Link.exe')),('/win32manifest:' + (Join-Path $PSScriptRoot 'app.manifest')))
if ($Check) { $arguments = @('/nologo','/target:exe','/platform:x64','/optimize+',('/out:' + (Join-Path $output 'Link.Check.exe'))) }
$icon = Join-Path $PSScriptRoot 'assets\Link.ico'
$arguments += @(('/win32icon:' + $icon), ('/resource:' + $icon + ',Link.AppIcon'))
$arguments += '/resource:' + (Join-Path $PSScriptRoot 'layer2-network.ps1') + ',Link.Layer2Network'
foreach($script in @('install-layer2.ps1','prepare-layer2.ps1','manage-components.ps1','uninstall.ps1')){$arguments += '/resource:' + (Join-Path $PSScriptRoot $script) + ',Link.Script.' + $script}
$arguments += $references | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$arguments += Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' | Select-Object -ExpandProperty FullName
& (Join-Path $framework 'csc.exe') @arguments
if ($LASTEXITCODE -ne 0) { throw 'Client compilation failed' }
Write-Output 'Link.exe compiled.'

if (-not $Check -and -not $CompileOnly) {
    if (-not $Python) {
        $Python = if (Get-Command py.exe -ErrorAction SilentlyContinue) { 'py.exe' } elseif (Get-Command python.exe -ErrorAction SilentlyContinue) { 'python.exe' } else { throw 'Full client build requires Python 3.9+. Install Python or use -CompileOnly for source compilation.' }
    }
    [string[]]$pythonArgs = @(); if ([IO.Path]::GetFileName($Python) -eq 'py.exe') { $pythonArgs = @('-3') }
    & $Python @pythonArgs -c 'import sys; sys.exit(0 if sys.version_info >= (3,9) else 1)'
    if ($LASTEXITCODE -ne 0) { throw 'Full client build requires Python 3.9+.' }
    & $Python @pythonArgs (Join-Path $project 'deploy\fetch_clients.py') (Join-Path $project 'artifacts\vendor') --platform windows --client-output $output
    if ($LASTEXITCODE -ne 0) { throw 'Runtime download/verification failed. Check access to github.com and www.wintun.net; valid cached archives also work offline.' }
    & (Join-Path $project 'deploy\build-uninstallers.ps1') -ClientOnly
    foreach ($name in @('LICENSE','THIRD-PARTY-NOTICES.md')) { Copy-Item -LiteralPath (Join-Path $project $name) -Destination $output -Force }
    Copy-Item -LiteralPath (Join-Path $project 'third_party') -Destination $output -Recurse -Force
    Write-Output ('Complete Windows client: ' + $output)
}
