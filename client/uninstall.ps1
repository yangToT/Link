param([switch]$RemoveIdentity,[switch]$Plan,[switch]$Layer2Only,[switch]$KeepComponentCache,[string]$SourceRoot)
# Run in an elevated PowerShell. Only Link-owned services, rules and files are affected.
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding=New-Object Text.UTF8Encoding($false)
$program = [IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'Link'))
$data = [IO.Path]::GetFullPath((Join-Path $env:ProgramData 'Link'))
# Verify every service before changing any of them; shared installations are excluded.
$ownedServices=@()
foreach($name in @('LinkAgent','SEVPNCLIENT','SEVPNBRIDGE','SEVPNCLIENTDEV','SEVPNBRIDGEDEV')) {
    $key=Get-ItemProperty -LiteralPath ('HKLM:\SYSTEM\CurrentControlSet\Services\'+$name) -ErrorAction SilentlyContinue
    if(-not $key){continue}
    $raw=[Environment]::ExpandEnvironmentVariables([string]$key.ImagePath)
    $exe=if($raw.StartsWith('"')){($raw -split '"')[1]}else{($raw -split '\s+')[0]}
    $owned=[IO.Path]::GetFullPath($exe).StartsWith($program+'\',[StringComparison]::OrdinalIgnoreCase)
    if($name -eq 'LinkAgent' -and -not $owned){throw 'LinkAgent belongs to another installation'}
    if($owned){$ownedServices+=$name}
}
function Assert-Tree([string]$path) {
    if(-not (Test-Path -LiteralPath $path)){return}
    $queue=New-Object 'Collections.Generic.Queue[string]';$queue.Enqueue($path)
    while($queue.Count){$current=$queue.Dequeue();if((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Installation reparse point requires review'};foreach($item in Get-ChildItem -LiteralPath $current -Force){if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Installation reparse point requires review'};if($item.PSIsContainer){$queue.Enqueue($item.FullName)}}}
}
Assert-Tree $program
if($RemoveIdentity){Assert-Tree $data}
$ownership=Join-Path $data 'install-owned.json'
if(Test-Path -LiteralPath $ownership){$record=Get-Content -LiteralPath $ownership -Raw -Encoding UTF8 | ConvertFrom-Json;if($record.program -ne $program){throw 'Installation ownership record mismatch'}}
if((Test-Path -LiteralPath $program) -and -not ($ownedServices -contains 'LinkAgent') -and -not (Test-Path -LiteralPath (Join-Path $data 'install-owned.json'))){throw 'Cannot prove installation ownership'}
if($Layer2Only){$ownedServices=@($ownedServices | Where-Object {$_ -ne 'LinkAgent'})}
function Test-LinkExecutable([string]$path){
    # Read .NET metadata only; do not load or execute an unknown program.
    try{
        if([Reflection.AssemblyName]::GetAssemblyName($path).Name -ne 'Link'){return $false}
        # Framework reflection-only contexts cache assembly identities. Read each
        # version in a fresh metadata-only process so older copies are recognized.
        $encodedPath=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($path))
        $code="try{`$p=[Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('$encodedPath'));`$r=[Reflection.Assembly]::ReflectionOnlyLoad([IO.File]::ReadAllBytes(`$p)).GetManifestResourceNames();if(`$r -contains 'Link.AppIcon' -and `$r -contains 'Link.Layer2Network' -and `$r -contains 'Link.Script.uninstall.ps1'){'LINK_METADATA_OK'}}catch{exit 1}"
        $info=New-Object Diagnostics.ProcessStartInfo
        $info.FileName=Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $info.Arguments='-NoProfile -NonInteractive -EncodedCommand '+[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($code))
        $info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
        $probe=[Diagnostics.Process]::Start($info)
        try{if(-not $probe.WaitForExit(10000)){$probe.Kill();throw 'Metadata probe timed out'};return $probe.ExitCode -eq 0 -and $probe.StandardOutput.ReadToEnd().Trim() -eq 'LINK_METADATA_OK'}finally{$probe.Dispose()}
    }catch{return $false}
}
$portableFiles=@()
if(-not $Layer2Only){
    $referenceRoots=@($program)
    if($SourceRoot){
        $source=[IO.Path]::GetFullPath($SourceRoot).TrimEnd('\')
        if($source -ne $program){
            $sourceExe=Join-Path $source 'Link.exe'
            if(-not (Test-Path -LiteralPath $sourceExe) -or -not (Test-LinkExecutable $sourceExe)){throw 'Uninstaller must be beside the Link client program'}
            $referenceRoots+=$source
        }
    }
    $hashes=@{};$files=@('Link.exe','netbird.exe','wintun.dll','Uninstall.exe','uninstall.ps1')
    foreach($base in $referenceRoots){foreach($name in $files){$file=Join-Path $base $name;if(Test-Path -LiteralPath $file){if((Get-Item -LiteralPath $file -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Package file is a reparse point'};$hashes[(Get-FileHash -LiteralPath $file).Hash]=$true}}}
    # Older clients can still be running from an extracted package. Match their
    # actual bytes or Link-specific managed resources, never kill by filename alone.
    $activeCopies=@(Get-CimInstance Win32_Process | Where-Object {$_.Name -eq 'Link.exe' -and $_.ExecutablePath -and (Test-Path -LiteralPath $_.ExecutablePath) -and ($hashes.ContainsKey((Get-FileHash -LiteralPath $_.ExecutablePath).Hash) -or (Test-LinkExecutable $_.ExecutablePath))})
    foreach($copy in $activeCopies){$hashes[(Get-FileHash -LiteralPath $copy.ExecutablePath).Hash]=$true}
    $packageRoots=@($referenceRoots+@($activeCopies | ForEach-Object {Split-Path $_.ExecutablePath -Parent}) | Select-Object -Unique)
    # Retain the scope across retries: a failed cleanup may already have stopped
    # a portable process, so the next attempt cannot rediscover it by process ID.
    if($record -and $record.uninstallPending){foreach($saved in @($record.portableFiles)){
        if(-not $saved.path){continue}
        $file=[IO.Path]::GetFullPath([string]$saved.path)
        if([IO.Path]::GetFileName($file) -notin $files){throw 'Invalid saved portable file'}
        if(Test-Path -LiteralPath $file){
            if((Get-FileHash -LiteralPath $file).Hash -ne $saved.hash){throw 'Saved portable file changed; review before retry'}
            $hashes[$saved.hash]=$true;$packageRoots+=Split-Path $file -Parent
        }
    }}
    foreach($base in $packageRoots | Select-Object -Unique | Where-Object {$_ -ne $program}){
        # Never recursively remove an extraction folder; it can contain user work.
        $ancestor=Get-Item -LiteralPath $base -Force
        while($ancestor){if($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Package path is a reparse point'};$ancestor=$ancestor.Parent}
        foreach($name in $files){$file=Join-Path $base $name;if(Test-Path -LiteralPath $file){if((Get-Item -LiteralPath $file -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Package file is a reparse point'};if($hashes.ContainsKey((Get-FileHash -LiteralPath $file).Hash)){$portableFiles+=$file}}}
    }
}
function Stop-OwnedProcess($process){
    $current=Get-Process -Id $process.ProcessId -ErrorAction SilentlyContinue
    if(-not $current){return}
    try{
        if(-not $current.MainModule.FileName.Equals($process.ExecutablePath,[StringComparison]::OrdinalIgnoreCase)){throw 'Process identity changed; retry uninstall'}
        $current.Kill();if(-not $current.WaitForExit(15000)){throw 'Owned process did not exit'}
    }finally{$current.Dispose()}
}
if($Plan){[pscustomobject]@{program=$program;data=$data;services=$ownedServices;portableFiles=$portableFiles;removeIdentity=[bool]$RemoveIdentity;layer2Only=[bool]$Layer2Only}|ConvertTo-Json;return}
$principal=New-Object Security.Principal.WindowsPrincipal ([Security.Principal.WindowsIdentity]::GetCurrent())
if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Run as administrator'}
$mutex=$null;$acquired=$false
if(-not $Layer2Only){$mutex=New-Object Threading.Mutex($false,'Global\Link.ComponentMaintenance');try{$acquired=$mutex.WaitOne(0)}catch [Threading.AbandonedMutexException]{$acquired=$true};if(-not $acquired){$mutex.Dispose();throw 'Another installation or component operation is running'}}
try {
Write-Output 'LINK_STAGE|正在断开连接并停止后台服务'
if(-not $Layer2Only){
if(Test-Path -LiteralPath $data){@{program=$program;uninstallPending=$true;portableFiles=@($portableFiles | ForEach-Object {@{path=$_;hash=(Get-FileHash -LiteralPath $_).Hash}})}|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $data 'install-owned.json') -Encoding UTF8}
$service = Get-Service -Name LinkAgent -ErrorAction SilentlyContinue
if ($service) {
    & sc.exe config LinkAgent start= disabled | Out-Null
    if($LASTEXITCODE -ne 0){throw 'Cannot disable LinkAgent restart'}
    if ($service.Status -ne 'Stopped') { Stop-Service -Name LinkAgent }
    try{$service.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(150))}finally{$service.Dispose();$service=$null}
}
foreach($copy in $activeCopies){Stop-OwnedProcess $copy}
Get-CimInstance Win32_Process | Where-Object {$_.ExecutablePath -and ([IO.Path]::GetFullPath($_.ExecutablePath)).StartsWith($program+'\',[StringComparison]::OrdinalIgnoreCase) -and $_.Name -in @('Link.exe','netbird.exe')} | ForEach-Object {Stop-OwnedProcess $_}
}else{
    $disable=Start-Process -FilePath (Join-Path $program 'Link.exe') -ArgumentList '--disable-layer2' -WindowStyle Hidden -Wait -PassThru
    if($disable.ExitCode -ne 0){throw 'Stop LAN access before component removal; recovery data retained'}
}
Write-Output 'LINK_STAGE|正在恢复 Link 网络设置'
$installed = Join-Path $env:ProgramFiles 'Link\Link.exe'
$journal = Join-Path $env:ProgramData 'Link\layer2-journal.json'
if (Test-Path -LiteralPath $journal) {
    if (-not (Test-Path -LiteralPath $installed)) { throw 'Restore the Link executable before cleaning its network resources.' }
    $cleanup = Start-Process -FilePath $installed -ArgumentList '--cleanup-layer2' -WindowStyle Hidden -Wait -PassThru
    if ($cleanup.ExitCode -ne 0 -or (Test-Path -LiteralPath $journal)) { throw 'Network cleanup incomplete; recovery data and installation retained.' }
}
if(-not $Layer2Only -and (Get-NetAdapter -IncludeHidden -ErrorAction SilentlyContinue | Where-Object {$_.Name -eq 'Link0'})){throw 'Link0 remains after disconnect; recovery data retained'}
foreach($name in $ownedServices | Where-Object {$_ -ne 'LinkAgent'}) {
    $component=Get-Service $name -ErrorAction SilentlyContinue
    if($component){try{if($component.Status -ne 'Stopped'){Stop-Service $name;$component.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(60))}}finally{$component.Dispose()}}
}
# SeLow is a shared Windows protocol driver. Remove it only when our install
# journal proves it was absent beforehand, and no other SoftEther service exists.
function Remove-OwnedLayer2Driver {
$componentJournal=Join-Path $data 'layer2-install.json'
if(Test-Path -LiteralPath $componentJournal){
    $record=Get-Content -LiteralPath $componentJournal -Raw -Encoding UTF8 | ConvertFrom-Json
    if($record.program -ne (Join-Path $program 'softether')){throw 'Component journal ownership mismatch'}
    $newSeLow=$record.PSObject.Properties.Name -contains 'driversBefore' -and @($record.driversBefore | Where-Object Name -eq 'SeLow').Count -eq 0
    $driver=Get-CimInstance Win32_SystemDriver -Filter "Name='SeLow'"
    if($newSeLow){
        $others=@(Get-CimInstance Win32_Service | Where-Object {($_.Name -match '^SEVPN' -or $_.PathName -match 'vpn(client|bridge|server)\.exe') -and $_.Name -notin $ownedServices})
        if($others.Count){Write-Output 'SeLow retained: another SoftEther installation uses shared components.'}
        else {
            if($driver){
            $expected=Join-Path $env:WINDIR 'System32\drivers\SeLow_x64.sys'
            if([IO.Path]::GetFullPath($driver.PathName) -ne $expected){throw 'Unexpected SeLow driver path; recovery data retained'}
            $signature=Get-AuthenticodeSignature -LiteralPath $expected
            if($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'SOFTETHER CORPORATION|Microsoft Windows Hardware Compatibility Publisher'){throw 'Unexpected SeLow driver signature; recovery data retained'}
            # /u removes only this protocol; /d (all networking) must never be used.
            & netcfg.exe /u SeLow | Out-Null
            if($LASTEXITCODE -ne 0){throw 'SeLow removal incomplete or reboot required; recovery data retained'}
            if(Get-NetAdapterBinding -AllBindings | Where-Object ComponentID -eq 'SeLow'){throw 'SeLow bindings remain; recovery data retained'}
            }
            if($record.PSObject.Properties.Name -contains 'driverPackagesBefore' -or $record.PSObject.Properties.Name -contains 'driverPackagesCreated'){
                $packages=@(Get-WindowsDriver -Online -All)
                $ownedPackages=if($record.PSObject.Properties.Name -contains 'driverPackagesBefore'){@($record.driverPackagesAfter | Where-Object {$_.Driver -notin @($record.driverPackagesBefore.Driver)})}else{@($record.driverPackagesCreated)}
                foreach($package in $ownedPackages){
                    if($package.Driver -notmatch '^oem\d+\.inf$' -or $package.OriginalFileName -notmatch '[\\/]selow[^\\/]*\.inf$'){throw 'Invalid recorded driver package'}
                    $current=@($packages | Where-Object Driver -eq $package.Driver)
                    if(-not $current.Count){continue}
                    if($current.Count -ne 1 -or $current[0].OriginalFileName -ne $package.OriginalFileName -or $current[0].ProviderName -ne $package.ProviderName -or $current[0].Version -ne $package.Version){throw 'Driver package identity changed; recovery data retained'}
                    & pnputil.exe /delete-driver $package.Driver | Out-Null
                    if($LASTEXITCODE -ne 0){throw 'Owned driver package still in use; recovery data retained'}
                }
            }else{Write-Output 'Driver store package retained: no pre-install package inventory is available.'}
        }
    }
}
}
Write-Output 'LINK_STAGE|正在移除专属组件与驱动'
Remove-OwnedLayer2Driver
foreach($name in $ownedServices){& sc.exe delete $name | Out-Null;if($LASTEXITCODE -notin @(0,1060)){throw ('Cannot remove owned service '+$name)}}
# A successful sc delete can mean marked-for-deletion. Do not report success
# while another process still holds the service open.
foreach($name in $ownedServices){
    $deadline=[DateTime]::UtcNow.AddSeconds(15)
    do{$remaining=Get-Service -Name $name -ErrorAction SilentlyContinue;if(-not $remaining){break};$remaining.Dispose();Start-Sleep -Milliseconds 250}while([DateTime]::UtcNow -lt $deadline)
    if($remaining){throw ('Service still pending deletion; close service management windows or restart Windows and retry: '+$name)}
}
Write-Output 'LINK_STAGE|正在清理 Link 规则与程序文件'
if(-not $Layer2Only){Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -like 'Link-Map-*' -or $_.DisplayName -like 'Link-Service-*' } | Remove-NetFirewallRule}
foreach($name in @('Link-Layer2-client','Link-Layer2-bridge')) {
    $rule=Get-NetFirewallRule -Name $name -ErrorAction SilentlyContinue
    if($rule){
        $filter=$rule | Get-NetFirewallApplicationFilter
        if($filter.Program -and ([IO.Path]::GetFullPath($filter.Program)).StartsWith($program+'\softether\',[StringComparison]::OrdinalIgnoreCase)){$rule | Remove-NetFirewallRule}
    }
}
if($Layer2Only){
    $componentRoot=[IO.Path]::GetFullPath((Join-Path $program 'softether'))
    if($componentRoot -ne ([IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'Link'))+'\softether')){throw 'Unsafe component path'}
    if(Test-Path -LiteralPath $componentRoot){Remove-Item -LiteralPath $componentRoot -Recurse -Force}
    foreach($name in @('layer2-local.bin','layer2-install.json','layer2-server.pem','layer2-driver-install-evidence.txt')){Remove-Item -LiteralPath (Join-Path $data $name) -Force -ErrorAction SilentlyContinue}
    if(-not $KeepComponentCache){$cache=[IO.Path]::GetFullPath((Join-Path $data 'component-downloads'));if($cache -ne ([IO.Path]::GetFullPath((Join-Path $env:ProgramData 'Link'))+'\component-downloads')){throw 'Unsafe cache path'};Assert-Tree $cache;if(Test-Path -LiteralPath $cache){Remove-Item -LiteralPath $cache -Recurse -Force}}
    if(Test-Path -LiteralPath $componentRoot){throw 'Component files remain'}
    Write-Output 'Optional LAN components removed; Link identity and base connection retained.';return
}
$data = [IO.Path]::GetFullPath((Join-Path $env:ProgramData 'Link'))
$program = [IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'Link'))
if ($data -ne ([IO.Path]::GetFullPath($env:ProgramData).TrimEnd('\') + '\Link')) { throw 'Unsafe data path' }
if ($program -ne ([IO.Path]::GetFullPath($env:ProgramFiles).TrimEnd('\') + '\Link')) { throw 'Unsafe program path' }
if ($RemoveIdentity -and (Test-Path -LiteralPath $data)) {
    $pem = Join-Path $data 'ca.pem'
    if (Test-Path -LiteralPath $pem) {
        $text = (Get-Content -LiteralPath $pem -Raw).Replace('-----BEGIN CERTIFICATE-----','').Replace('-----END CERTIFICATE-----','')
        $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList @(,[Convert]::FromBase64String($text))
        $store = New-Object System.Security.Cryptography.X509Certificates.X509Store 'Root','LocalMachine'
        try { $store.Open('ReadWrite'); $store.Remove($cert) } finally { $store.Close() }
    }
}
foreach($file in $portableFiles){if(Test-Path -LiteralPath $file){Remove-Item -LiteralPath $file -Force}}
if (Test-Path -LiteralPath $program) { Remove-Item -LiteralPath $program -Recurse -Force }
$uninstallKey='HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Link.Client'
$registration=Get-ItemProperty -LiteralPath $uninstallKey -ErrorAction SilentlyContinue
if($registration -and $registration.InstallLocation -eq $program){Remove-Item -LiteralPath $uninstallKey}
if(Get-CimInstance Win32_Process | Where-Object {$_.ExecutablePath -and ($_.ExecutablePath.StartsWith($program+'\',[StringComparison]::OrdinalIgnoreCase) -or $_.ExecutablePath -in $portableFiles)}){throw 'Link processes remain; identity retained'}
if ($RemoveIdentity -and (Test-Path -LiteralPath $data)) { Remove-Item -LiteralPath $data -Recurse -Force }
if ($RemoveIdentity) { Remove-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Link' -Name OwnerSID -ErrorAction SilentlyContinue }
Write-Output 'LINK_STAGE|正在核验卸载结果'
if(Test-Path -LiteralPath $program){throw 'Installation files remain'}
if($RemoveIdentity -and (Test-Path -LiteralPath $data)){throw 'Identity files remain'}
foreach($file in $portableFiles){if(Test-Path -LiteralPath $file){throw 'Portable program files remain'}}
if(Get-CimInstance Win32_Process | Where-Object {$_.ExecutablePath -and ($_.ExecutablePath.StartsWith($program+'\',[StringComparison]::OrdinalIgnoreCase) -or $_.ExecutablePath -in $portableFiles)}){throw 'Link processes remain'}
Write-Output 'Link removed. Device identity is retained unless -RemoveIdentity was supplied.'
Write-Output 'LINK_COMPLETE|uninstall'
}finally{if($mutex){if($acquired){$mutex.ReleaseMutex()};$mutex.Dispose()}}
