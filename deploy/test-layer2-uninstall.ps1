# Execute the actual cleanup function with command doubles; never change host drivers.
$ErrorActionPreference='Stop'
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot '..\client\uninstall.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Uninstaller parse error'}
$definition=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Remove-OwnedLayer2Driver'},$true)
if(-not $definition){throw 'Cleanup function not found'}
. ([scriptblock]::Create($definition.Extent.Text))
$data=Join-Path ([IO.Path]::GetTempPath()) ('Link-Driver-Test-'+[guid]::NewGuid().ToString('N'))
$program=Join-Path $data 'program';$ownedServices=@('SEVPNCLIENT','SEVPNBRIDGE')
New-Item -ItemType Directory -Path $data | Out-Null
$script:calls=New-Object 'Collections.Generic.List[string]'
$script:foreign=$false;$script:driverPresent=$true;$script:packageChanged=$false;$script:failure=$false
$package=[pscustomobject]@{Driver='oem123.inf';OriginalFileName='C:\Windows\System32\DriverStore\FileRepository\selow_x64\selow_x64.inf';ProviderName='SoftEther Corporation';Version='4.25'}
function Get-CimInstance {param($ClassName,$Filter)
 if($ClassName -eq 'Win32_SystemDriver'){if($script:driverPresent){[pscustomobject]@{PathName=(Join-Path $env:WINDIR 'System32\drivers\SeLow_x64.sys')}}}
 elseif($script:foreign){[pscustomobject]@{Name='SEVPNSERVER';PathName='C:\Other\vpnserver.exe'}}
}
function Get-AuthenticodeSignature {param($LiteralPath) [pscustomobject]@{Status='Valid';SignerCertificate=[pscustomobject]@{Subject='CN=Microsoft Windows Hardware Compatibility Publisher'}}}
function Get-NetAdapterBinding {param([switch]$AllBindings)}
function Get-WindowsDriver {param([switch]$Online,[switch]$All) if($script:packageChanged){[pscustomobject]@{Driver=$package.Driver;OriginalFileName='C:\Other\unrelated.inf';ProviderName='Other';Version='1'}}else{$package}}
function netcfg.exe {$script:calls.Add('netcfg '+($args -join ' '));$global:LASTEXITCODE=0}
function pnputil.exe {$script:calls.Add('pnputil '+($args -join ' '));$global:LASTEXITCODE=if($script:failure){5}else{0}}
function Record($preexisting,$priorPackage){
 @{program=(Join-Path $program 'softether');driversBefore=@(if($preexisting){@{Name='SeLow'}});driverPackagesBefore=@(if($priorPackage){$package});driverPackagesAfter=@($package)} | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $data 'layer2-install.json')
 $script:calls.Clear()
}
try{
 Record $true $false;Remove-OwnedLayer2Driver
 if($script:calls.Count){throw 'Pre-existing driver was touched'}
 Record $false $false;$script:foreign=$true;Remove-OwnedLayer2Driver
 if($script:calls.Count){throw 'Shared driver was touched'}
 $script:foreign=$false;Record $false $true;Remove-OwnedLayer2Driver
 if(($script:calls -join ',') -ne 'netcfg /u SeLow'){throw 'Pre-existing driver package was deleted'}
 Record $false $false;Remove-OwnedLayer2Driver
 if(($script:calls -join ',') -ne 'netcfg /u SeLow,pnputil /delete-driver oem123.inf'){throw 'Owned driver cleanup failed'}
 Record $false $false;$script:driverPresent=$false;Remove-OwnedLayer2Driver
 if(($script:calls -join ',') -ne 'pnputil /delete-driver oem123.inf'){throw 'Interrupted cleanup did not resume'}
 $record=Get-Content (Join-Path $data 'layer2-install.json') -Raw | ConvertFrom-Json
 $record.PSObject.Properties.Remove('driverPackagesBefore')
 $record | Add-Member NoteProperty driverPackagesCreated @($package)
 $record | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $data 'layer2-install.json')
 $script:calls.Clear();Remove-OwnedLayer2Driver
 if(($script:calls -join ',') -ne 'pnputil /delete-driver oem123.inf'){throw 'Documented package creation was not handled'}
 Record $false $false;$script:packageChanged=$true;$rejected=$false
 try{Remove-OwnedLayer2Driver}catch{if($_.Exception.Message -like '*identity changed*'){$rejected=$true}else{throw}}
 if(-not $rejected -or $script:calls.Count){throw 'Reused OEM package was accepted'}
 Record $false $false;$script:packageChanged=$false;$script:failure=$true;$rejected=$false
 try{Remove-OwnedLayer2Driver}catch{if($_.Exception.Message -like '*still in use*'){$rejected=$true}else{throw}}
 if(-not $rejected){throw 'Driver removal failure ignored'}
 'PASS: owned-only driver cleanup, shared/pre-existing preservation, OEM identity, retry and failure handling'
}finally{
 $resolved=[IO.Path]::GetFullPath($data)
 if($resolved.StartsWith([IO.Path]::GetTempPath(),[StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($resolved) -like 'Link-Driver-Test-*'){Remove-Item -LiteralPath $resolved -Recurse -Force}
}
