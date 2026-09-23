# Explicit administrator setup for dedicated, already-installed SoftEther components.
# Does not download drivers, change networking, start services or claim readiness.
param(
 [Parameter(Mandatory=$true)][string]$VpnCmd,
 [Security.SecureString]$ClientPassword,
 [Security.SecureString]$BridgePassword
)
$ErrorActionPreference='Stop'
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
if(-not ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Run as administrator'}
$root=[IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'Link\softether'))+'\'
$exe=[IO.Path]::GetFullPath($VpnCmd)
if(-not $exe.StartsWith($root,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($exe) -ne 'vpncmd.exe' -or -not (Test-Path -LiteralPath $exe)){throw 'Use vpncmd.exe from the dedicated Link softether directory'}
if(-not $ClientPassword -and -not $BridgePassword){throw 'Supply credentials for at least one prepared component'}
$config=@{vpncmd=$exe}
foreach($item in @(@{name='clientPassword';service='SEVPNCLIENT';value=$ClientPassword},@{name='bridgePassword';service='SEVPNBRIDGE';value=$BridgePassword})){
 if(-not $item.value){continue}
 $variants=@($item.service,($item.service+'DEV')) | ForEach-Object {Get-ItemProperty -LiteralPath ('HKLM:\SYSTEM\CurrentControlSet\Services\'+$_) -ErrorAction SilentlyContinue}
 if(@($variants).Count -ne 1){throw 'Component missing or multiple variants installed'}
 $path=$variants.ImagePath
 if(-not $path.TrimStart('"').StartsWith($root,[StringComparison]::OrdinalIgnoreCase)){throw 'Existing SoftEther service is not owned by Link'}
 $pointer=[Runtime.InteropServices.Marshal]::SecureStringToGlobalAllocUnicode($item.value)
 try{$plain=[Runtime.InteropServices.Marshal]::PtrToStringUni($pointer);if($plain.Length -lt 20 -or $plain -match '[\r\n]'){throw 'Use an independent administrator password of at least 20 characters'};$config[$item.name]=$plain}
 finally{[Runtime.InteropServices.Marshal]::ZeroFreeGlobalAllocUnicode($pointer);$plain=$null}
}
Add-Type -AssemblyName System.Security
$folder=Join-Path $env:ProgramData 'Link'
if(-not (Test-Path -LiteralPath $folder)){New-Item -ItemType Directory -Path $folder | Out-Null}
$acl=New-Object Security.AccessControl.DirectorySecurity
$acl.SetAccessRuleProtection($true,$false)
foreach($sid in @('S-1-5-18','S-1-5-32-544')){$rule=New-Object Security.AccessControl.FileSystemAccessRule ([Security.Principal.SecurityIdentifier]$sid),'FullControl','ContainerInherit,ObjectInherit','None','Allow';$acl.AddAccessRule($rule)}
Set-Acl -LiteralPath $folder -AclObject $acl
$target=Join-Path $folder 'layer2-local.bin'
if(Test-Path -LiteralPath $target){throw 'Configuration already exists; retain it until a deliberate credential rotation'}
$bytes=[Text.Encoding]::UTF8.GetBytes(($config|ConvertTo-Json -Compress))
try{[IO.File]::WriteAllBytes($target,[Security.Cryptography.ProtectedData]::Protect($bytes,$null,[Security.Cryptography.DataProtectionScope]::LocalMachine))}
finally{[Array]::Clear($bytes,0,$bytes.Length);$config.Clear()}
Write-Output 'Local component credentials saved with DPAPI. No network connection was started.'
