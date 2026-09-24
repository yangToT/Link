# Runs the embedded guard with in-memory cmdlet doubles. No system network changes.
$ErrorActionPreference='Stop'
$testDir=Join-Path ([IO.Path]::GetTempPath()) ('Link-Guard-Test-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testDir | Out-Null
$global:LinkGuardTestnic=$null;
$global:LinkGuardTestroutes=@();$global:LinkGuardTestremoved=@();$global:LinkGuardTestintercept=$false
function Get-NetAdapter { param([switch]$IncludeHidden)
 if($global:LinkGuardTestnic){$global:LinkGuardTestnic}
 [pscustomobject]@{InterfaceGuid=[guid]'11111111-1111-1111-1111-111111111111';ifIndex=6;HardwareInterface=$true;Status='Up';PhysicalMediaType='802.3';Name='Ethernet'}
 [pscustomobject]@{InterfaceGuid=[guid]'22222222-2222-2222-2222-222222222222';ifIndex=9;HardwareInterface=$false;Status='Up';Name='Link0'}
}
function Get-NetRoute { param($DestinationPrefix,$InterfaceIndex,$ErrorAction)
 $global:LinkGuardTestroutes | Where-Object {(-not $DestinationPrefix -or $_.DestinationPrefix -eq $DestinationPrefix) -and (-not $InterfaceIndex -or $_.InterfaceIndex -eq $InterfaceIndex)}
}
function New-NetRoute { param($DestinationPrefix,$InterfaceIndex,$NextHop,$RouteMetric,$PolicyStore)
 $global:LinkGuardTestroutes+= [pscustomobject]@{DestinationPrefix=$DestinationPrefix;InterfaceIndex=$InterfaceIndex;NextHop=$NextHop;RouteMetric=$RouteMetric;Protocol='NetMgmt'}
}
function Remove-NetRoute { param([Parameter(ValueFromPipeline=$true)]$InputObject,[switch]$Confirm)
 process {$global:LinkGuardTestremoved+=$InputObject;$global:LinkGuardTestroutes=@($global:LinkGuardTestroutes | Where-Object {$_ -ne $InputObject})}
}
function Find-NetRoute {param($RemoteIPAddress,$ErrorAction)
 if($RemoteIPAddress -eq '10.20.0.1' -and $global:LinkGuardTestlanMissing){return}
 if($RemoteIPAddress -eq '10.20.0.1' -and $global:LinkGuardTestlanOther){[pscustomobject]@{DestinationPrefix='0.0.0.0/0';InterfaceIndex=6};return}
 [pscustomobject]@{DestinationPrefix='0.0.0.0/0';InterfaceIndex=$(if($global:LinkGuardTestintercept){99}elseif($RemoteIPAddress -eq '100.88.0.1'){9}elseif($RemoteIPAddress -eq '10.20.0.1'){11}else{6})}
}
function Get-DnsClientServerAddress {param($InterfaceIndex,$AddressFamily)
 [pscustomobject]@{ServerAddresses=$(if($InterfaceIndex -eq 11){$global:LinkGuardTestdns}else{@('192.168.30.1')})}
}
function Set-DnsClientServerAddress {param($InterfaceIndex,$ServerAddresses)
 if($InterfaceIndex -ne 11){throw 'Physical DNS modified'};$global:LinkGuardTestdns=@($ServerAddresses)
}
function Get-NetIPAddress {param($InterfaceIndex,$AddressFamily,$ErrorAction)
 if($global:LinkGuardTestaddressMissing){if($ErrorAction -ne 'SilentlyContinue'){throw 'CIM address temporarily missing'};return}
 [pscustomobject]@{IPAddress='10.20.0.10';AddressState='Preferred';PrefixOrigin='Dhcp'}
}
try {
 $p=@{account='Link-ABCDEF012345';nic='LNKABCDEF012345';adapterId='{11111111-1111-1111-1111-111111111111}';publicServer='203.0.113.1';overlayEndpoint='100.88.0.1';gateway='192.168.30.1';role='member';action='pin';adapterName=([string][char]0x4ee5+[char]0x592a+[char]0x7f51)}
 $request=Join-Path $testDir 'request.json'
 $guard=Join-Path $PSScriptRoot 'layer2-network.ps1'
 function Invoke-Guard {[IO.File]::WriteAllText($request,($p | ConvertTo-Json),(New-Object Text.UTF8Encoding($false)));$result=& $guard -Request $request;if(($result | ConvertFrom-Json).error){throw ($result | ConvertFrom-Json).error};$result}
 # A member has no virtual adapter yet during pin; it must still succeed.
 $null=Invoke-Guard
 if($global:LinkGuardTestroutes.Count -ne 1){throw 'Pin was not created'}
 $foreign=[pscustomobject]@{DestinationPrefix='203.0.113.1/32';InterfaceIndex=20;NextHop='172.18.0.2';RouteMetric=1;Protocol='NetMgmt'}
 $global:LinkGuardTestroutes+=$foreign;$p.action='cleanup';$null=Invoke-Guard
 if($global:LinkGuardTestroutes.Count -ne 1 -or $global:LinkGuardTestroutes[0] -ne $foreign){throw 'Foreign route changed'}
 $null=Invoke-Guard;if($global:LinkGuardTestremoved.Count -ne 1){throw 'Cleanup not idempotent'}
 # An existing /32 owned elsewhere must not be recorded or removed by Link.
 $p.action='pin';$null=Invoke-Guard;$p.action='cleanup';$null=Invoke-Guard
 if($global:LinkGuardTestroutes.Count -ne 1 -or (Test-Path (Join-Path $testDir 'layer2-route-owner.json'))){throw 'Claimed foreign route'}
 $global:LinkGuardTestnic=$null;
$global:LinkGuardTestroutes=@();$p.action='pin';$global:LinkGuardTestintercept=$true;$rejected=$false
 try {$null=Invoke-Guard} catch {$rejected=$true}
 if(-not $rejected -or -not (Test-Path (Join-Path $testDir 'layer2-route-owner.json'))){throw 'Interception or recovery record not detected'}
 $p.action='cleanup';$null=Invoke-Guard;if($global:LinkGuardTestroutes.Count -ne 0){throw 'Failed pin left route behind'}
 $global:LinkGuardTestnic=[pscustomobject]@{InterfaceGuid=[guid]'33333333-3333-3333-3333-333333333333';InterfaceDescription='VPN Client Adapter - VPN127';ifIndex=11;Name='Ethernet 3'}
 $p.nic='VPN127';$p.action='identify';$identity=Invoke-Guard | ConvertFrom-Json
 if($identity.nicId -ne '33333333-3333-3333-3333-333333333333'){throw 'NIC identity not recorded'}
 $p.nicId=$identity.nicId;$p.action='verify-nic';if(-not (Invoke-Guard | ConvertFrom-Json).nicPresent){throw 'Owned adapter rejected'}
 $p.nicId='44444444-4444-4444-4444-444444444444';$rejected=$false;try{$null=Invoke-Guard}catch{$rejected=$true};if(-not $rejected){throw 'Foreign replacement adapter accepted'}
 foreach($name in @('VPN1','VPN0','VPN128','VPN999')){$p.nic=$name;$rejected=$false;try{$null=Invoke-Guard}catch{$rejected=$true};if(-not $rejected){throw 'Unregulated adapter name accepted'}}
 $p.nic='VPN127';$global:LinkGuardTestnic=$null;if((Invoke-Guard | ConvertFrom-Json).nicPresent){throw 'Missing adapter not idempotent'}
 $global:LinkGuardTestintercept=$false;$global:LinkGuardTestdns=@('10.20.0.1')
 $global:LinkGuardTestnic=[pscustomobject]@{InterfaceGuid=[guid]'33333333-3333-3333-3333-333333333333';InterfaceDescription='VPN Client Adapter - VPN127';ifIndex=11;Name='Ethernet 3'}
 $p.nicId='33333333-3333-3333-3333-333333333333';$p.action='check';$p.networks=@('10.20.0.0/24');$p.remoteGateway='10.20.0.1'
 $foreign=[pscustomobject]@{DestinationPrefix='0.0.0.0/0';InterfaceIndex=6;NextHop='192.168.30.1';Protocol='Dhcp'}
 $global:LinkGuardTestroutes=@($foreign,[pscustomobject]@{DestinationPrefix='0.0.0.0/0';InterfaceIndex=11;NextHop='10.20.0.1';Protocol='Dhcp'},[pscustomobject]@{DestinationPrefix='10.20.0.0/24';InterfaceIndex=11;NextHop='0.0.0.0';Protocol='Local'})
 $null=Invoke-Guard
 if(@($global:LinkGuardTestroutes | Where-Object DestinationPrefix -eq '0.0.0.0/0').Count -ne 1 -or $global:LinkGuardTestroutes -notcontains $foreign){throw 'Host default route changed'}
 if(($global:LinkGuardTestdns -join ',') -ne '192.168.30.1'){throw 'Remote DHCP DNS accepted'}
 $global:LinkGuardTestroutes+=[pscustomobject]@{DestinationPrefix='0.0.0.0/0';InterfaceIndex=11;NextHop='10.20.0.1';Protocol='NetMgmt'}
 $null=Invoke-Guard;if(@($global:LinkGuardTestroutes | Where-Object {$_.InterfaceIndex -eq 11 -and $_.DestinationPrefix -eq '0.0.0.0/0'}).Count -gt 0 -or $global:LinkGuardTestroutes -notcontains $foreign){throw 'NetMgmt gateway suppression crossed ownership boundary'}
 # A transient empty CIM address result is DHCP pending, not reason to destroy the NIC.
 $global:LinkGuardTestaddressMissing=$true;$pending=Invoke-Guard | ConvertFrom-Json
 if($pending.error -or $pending.waiting -or $global:LinkGuardTestroutes -notcontains $foreign){throw 'Missing DHCP address tore down or changed the transport'}
 $global:LinkGuardTestaddressMissing=$false
 if((Invoke-Guard | ConvertFrom-Json).error){throw 'Recovered DHCP address still fails'}
 # Missing or temporarily different best route is pending, not a destructive TUN failure.
 $global:LinkGuardTestlanOther=$true;$pending=Invoke-Guard | ConvertFrom-Json
 if($pending.waiting -ne 'lan-route' -or $pending.actualInterface -ne 6 -or $pending.expectedInterface -ne 11){throw 'Actual LAN route conflict evidence lost'}
 $global:LinkGuardTestlanMissing=$true;$pending=Invoke-Guard | ConvertFrom-Json
 if($pending.waiting -ne 'lan-route' -or $pending.actualInterface){throw 'Missing route destroyed connection instead of waiting'}
 $global:LinkGuardTestlanMissing=$false;$global:LinkGuardTestlanOther=$false
 if((Invoke-Guard | ConvertFrom-Json).waiting){throw 'Restored LAN route remains pending'}
 if($global:LinkGuardTestroutes -notcontains $foreign){throw 'Route recovery changed another interface'}
 Write-Output 'PASS: LAN route transition is pending with actual interface evidence, then recovers'
 Write-Output 'PASS: transient missing DHCP address keeps the owned adapter and recovers'
 Write-Output 'PASS: DHCP/NetMgmt default removed only on owned NIC; local DNS retained; foreign routes protected'
 Write-Output 'PASS: regulated NIC names, GUID ownership, missing adapter cleanup'
 Write-Output 'PASS: pin before adapter creation, TUN interception, ownership-only cleanup, failed-pin recovery, idempotent cleanup'
} finally {
 Remove-Variable -Scope Global -Name LinkGuardTestnic,LinkGuardTestroutes,LinkGuardTestremoved,LinkGuardTestintercept,LinkGuardTestdns,LinkGuardTestlanOther,LinkGuardTestlanMissing,LinkGuardTestaddressMissing -ErrorAction SilentlyContinue
 # Only this test's generated temp directory is removed.
 if([IO.Path]::GetFullPath($testDir).StartsWith([IO.Path]::GetTempPath(),[StringComparison]::OrdinalIgnoreCase)){Remove-Item -LiteralPath $testDir -Recurse -Force}
}
