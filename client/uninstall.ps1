param([switch]$RemoveIdentity,[switch]$Plan)
# Run in an elevated PowerShell. Only Link-owned services, rules and files are affected.
$ErrorActionPreference = 'Stop'
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
if((Test-Path -LiteralPath $program) -and -not ($ownedServices -contains 'LinkAgent') -and -not (Test-Path -LiteralPath (Join-Path $data 'install-owned.json'))){throw 'Cannot prove installation ownership'}
if($Plan){[pscustomobject]@{program=$program;data=$data;services=$ownedServices;removeIdentity=[bool]$RemoveIdentity}|ConvertTo-Json;return}
$principal=New-Object Security.Principal.WindowsPrincipal ([Security.Principal.WindowsIdentity]::GetCurrent())
if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Run as administrator'}
if(Test-Path -LiteralPath $data){@{program=$program;uninstallPending=$true}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $data 'install-owned.json') -Encoding UTF8}
$service = Get-Service -Name LinkAgent -ErrorAction SilentlyContinue
if ($service) {
    & sc.exe config LinkAgent start= disabled | Out-Null
    if($LASTEXITCODE -ne 0){throw 'Cannot disable LinkAgent restart'}
    if ($service.Status -ne 'Stopped') { Stop-Service -Name LinkAgent }
    $service.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(150))
}
Get-CimInstance Win32_Process | Where-Object {$_.ExecutablePath -and ([IO.Path]::GetFullPath($_.ExecutablePath)).StartsWith($program+'\',[StringComparison]::OrdinalIgnoreCase) -and $_.Name -in @('Link.exe','netbird.exe')} | ForEach-Object {Stop-Process -Id $_.ProcessId -Force}
$installed = Join-Path $env:ProgramFiles 'Link\Link.exe'
$journal = Join-Path $env:ProgramData 'Link\layer2-journal.json'
if (Test-Path -LiteralPath $journal) {
    if (-not (Test-Path -LiteralPath $installed)) { throw 'Restore the Link executable before cleaning its network resources.' }
    $cleanup = Start-Process -FilePath $installed -ArgumentList '--cleanup-layer2' -WindowStyle Hidden -Wait -PassThru
    if ($cleanup.ExitCode -ne 0 -or (Test-Path -LiteralPath $journal)) { throw 'Network cleanup incomplete; recovery data and installation retained.' }
}
if(Get-NetAdapter -IncludeHidden -ErrorAction SilentlyContinue | Where-Object {$_.Name -eq 'Link0'}){throw 'Link0 remains after disconnect; recovery data retained'}
foreach($name in $ownedServices | Where-Object {$_ -ne 'LinkAgent'}) {
    $component=Get-Service $name -ErrorAction SilentlyContinue
    if($component -and $component.Status -ne 'Stopped'){Stop-Service $name;$component.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(60))}
}
foreach($name in $ownedServices){& sc.exe delete $name | Out-Null;if($LASTEXITCODE -notin @(0,1060)){throw ('Cannot remove owned service '+$name)}}
Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -like 'Link-Map-*' -or $_.DisplayName -like 'Link-Service-*' } | Remove-NetFirewallRule
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
if (Test-Path -LiteralPath $program) { Remove-Item -LiteralPath $program -Recurse -Force }
$uninstallKey='HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Link.Client'
$registration=Get-ItemProperty -LiteralPath $uninstallKey -ErrorAction SilentlyContinue
if($registration -and $registration.InstallLocation -eq $program){Remove-Item -LiteralPath $uninstallKey}
if ($RemoveIdentity -and (Test-Path -LiteralPath $data)) { Remove-Item -LiteralPath $data -Recurse -Force }
Write-Output 'Link removed. Device identity is retained unless -RemoveIdentity was supplied.'
