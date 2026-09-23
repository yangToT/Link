param([switch]$ClientOnly)
$ErrorActionPreference='Stop'
$project=Split-Path $PSScriptRoot -Parent
$framework=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$targets=if($ClientOnly){@('client')}else{@('client','server')}
foreach($target in $targets){
 $output=Join-Path $project ('artifacts\'+$target+'-windows-amd64')
 New-Item -ItemType Directory -Path $output -Force | Out-Null
 $script=if($target -eq 'client'){Join-Path $project 'client\uninstall.ps1'}else{Join-Path $PSScriptRoot 'uninstall-windows-server.ps1'}
 $arguments=@('/nologo','/target:winexe','/platform:x64','/optimize+',('/out:'+(Join-Path $output 'Uninstall.exe')),('/win32manifest:'+(Join-Path $project 'client\admin.manifest')),('/win32icon:'+(Join-Path $project 'client\assets\Link.ico')),('/resource:'+$script+',Link.Uninstall'),('/reference:'+(Join-Path $framework 'System.Windows.Forms.dll')),('/reference:'+(Join-Path $framework 'System.Core.dll')),('/reference:'+(Join-Path $framework 'System.Security.dll')))
 if($target -eq 'server'){$arguments+='/define:SERVER'}
 $arguments+=Join-Path $PSScriptRoot 'Uninstaller.cs'
 & (Join-Path $framework 'csc.exe') @arguments
 if($LASTEXITCODE -ne 0){throw 'Uninstaller build failed'}
 Copy-Item -LiteralPath $script -Destination (Join-Path $output 'uninstall.ps1') -Force
}
if ($ClientOnly) { Write-Output 'Built client Uninstall.exe.'; return }
$previousOS=$env:GOOS;$previousArch=$env:GOARCH;$previousCGO=$env:CGO_ENABLED
try{
 $env:GOOS='linux';$env:GOARCH='amd64';$env:CGO_ENABLED='0'
 $linux=Join-Path $project 'artifacts\server-linux-amd64'
 New-Item -ItemType Directory -Path (Join-Path $linux 'deploy') -Force | Out-Null
 go build -trimpath -ldflags='-s -w' -o (Join-Path $linux 'link-uninstall') (Join-Path $PSScriptRoot 'uninstall_linux.go')
 if($LASTEXITCODE -ne 0){throw 'Linux uninstaller build failed'}
 foreach($name in @('uninstall.py','uninstall.sh')){Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $linux ('deploy\'+$name)) -Force}
}finally{$env:GOOS=$previousOS;$env:GOARCH=$previousArch;$env:CGO_ENABLED=$previousCGO}
Write-Output 'Built client/server Uninstall.exe and Linux link-uninstall.'
