param([switch]$Check)
$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
$output = Join-Path $project 'artifacts\client-windows-amd64'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$references = @('System.dll','System.Core.dll','System.Security.dll','System.Web.Extensions.dll','System.ServiceProcess.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Xaml.dll','WPF\WindowsBase.dll','WPF\PresentationFramework.dll','WPF\PresentationCore.dll')
$arguments = @('/nologo','/target:winexe','/platform:x64','/optimize+',('/out:' + (Join-Path $output 'Link.exe')),('/win32manifest:' + (Join-Path $PSScriptRoot 'app.manifest')))
if ($Check) { $arguments = @('/nologo','/target:exe','/platform:x64','/optimize+',('/out:' + (Join-Path $output 'Link.Check.exe'))) }
$icon = Join-Path $PSScriptRoot 'assets\Link.ico'
$arguments += @(('/win32icon:' + $icon), ('/resource:' + $icon + ',Link.AppIcon'))
$arguments += $references | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$arguments += Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' | Select-Object -ExpandProperty FullName
& (Join-Path $framework 'csc.exe') @arguments
if ($LASTEXITCODE -ne 0) { throw 'Client compilation failed' }
Write-Output 'Link.exe compiled.'
