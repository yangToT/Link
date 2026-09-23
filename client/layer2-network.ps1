param([Parameter(Mandatory=$true)][string]$Request)
$ErrorActionPreference='Stop'
try {
$p=Get-Content -LiteralPath $Request -Raw -Encoding UTF8 | ConvertFrom-Json
if($p.account -notmatch '^Link-[A-F0-9]{12}$' -or $p.nic -notmatch '^(LNK[A-F0-9]{12}|VPN|VPN([2-9]|[1-9][0-9]|1[01][0-9]|12[0-7]))$'){throw 'Invalid resource ownership'}
if($p.action -in @('identify','verify-nic')){
 $virtual=@(Get-NetAdapter -IncludeHidden | Where-Object {$_.InterfaceDescription -eq ('VPN Client Adapter - '+$p.nic)})
 if($virtual.Count -gt 1){throw 'Ambiguous virtual adapter ownership'}
 if($p.action -eq 'identify'){
  if($virtual.Count -ne 1){throw 'Created adapter identity unavailable; retain recovery record'}
  @{nicId=$virtual[0].InterfaceGuid.ToString()} | ConvertTo-Json -Compress;exit
 }
 if($virtual.Count -eq 1 -and (!$p.nicId -or $virtual[0].InterfaceGuid.ToString().Trim('{}') -ne $p.nicId.Trim('{}'))){throw 'Adapter identity changed; refusing to remove another adapter'}
 @{nicPresent=($virtual.Count -eq 1)} | ConvertTo-Json -Compress;exit
}
$physical=Get-NetAdapter -IncludeHidden | Where-Object {$_.InterfaceGuid.ToString().Trim('{}') -eq $p.adapterId.Trim('{}')}
$ip=[Net.IPAddress]::Parse($p.publicServer)
if($ip.AddressFamily -ne 'InterNetwork'){throw 'IPv4 endpoint required'}
$prefix=$ip.ToString()+'/32'
$ownedRoute=[bool]$p.routeOwned
$ownerFile=Join-Path (Split-Path -Parent $Request) 'layer2-route-owner.json'
if($p.action -eq 'cleanup'){
 if(Test-Path -LiteralPath $ownerFile){
  $owner=Get-Content -LiteralPath $ownerFile -Raw -Encoding UTF8 | ConvertFrom-Json
  if($owner.account -ne $p.account -or $owner.prefix -ne $prefix -or $owner.adapterId -ne $p.adapterId){throw 'Route owner differs; recovery requires review'}
  if(-not $physical){throw 'Physical adapter unavailable; retain route recovery record'}
  Get-NetRoute -DestinationPrefix $prefix -InterfaceIndex $physical.ifIndex -ErrorAction SilentlyContinue | Where-Object {$_.NextHop -eq $owner.gateway -and $_.RouteMetric -eq 1 -and $_.Protocol -eq 'NetMgmt'} | Remove-NetRoute -Confirm:$false
  Remove-Item -LiteralPath $ownerFile
 }
 @{routeOwned=$false} | ConvertTo-Json -Compress; exit
}
if(-not $physical -or -not $physical.HardwareInterface -or $physical.Status -ne 'Up'){throw 'Physical transport adapter unavailable'}
if($p.role -eq 'entry' -and $physical.PhysicalMediaType -ne '802.3'){throw 'Wired entry required'}
if($p.action -eq 'pin'){
 $existing=@(Get-NetRoute -DestinationPrefix $prefix -ErrorAction SilentlyContinue)
 if($existing.Count -eq 0){
  @{account=$p.account;prefix=$prefix;adapterId=$p.adapterId;gateway=$p.gateway} | ConvertTo-Json -Compress | Set-Content -LiteralPath $ownerFile -Encoding UTF8
  New-NetRoute -DestinationPrefix $prefix -InterfaceIndex $physical.ifIndex -NextHop $p.gateway -RouteMetric 1 -PolicyStore ActiveStore | Out-Null;$ownedRoute=$true
 }
}
$best=Find-NetRoute -RemoteIPAddress $ip.ToString() | Where-Object {$_.PSObject.Properties['DestinationPrefix']} | Select-Object -First 1
if(-not $best -or $best.InterfaceIndex -ne $physical.ifIndex){throw 'TUN intercepts the Link transport endpoint'}
$overlay=Find-NetRoute -RemoteIPAddress $p.overlayEndpoint -ErrorAction SilentlyContinue | Where-Object {$_.PSObject.Properties['DestinationPrefix']} | Select-Object -First 1
$link=Get-NetAdapter -IncludeHidden | Where-Object {$_.Name -eq 'Link0'}
if(-not $link -or -not $overlay -or $overlay.InterfaceIndex -ne $link.ifIndex){@{waiting='overlay';actualInterface=$overlay.InterfaceIndex;expectedInterface=$link.ifIndex;prefix=$overlay.DestinationPrefix;routeOwned=$ownedRoute} | ConvertTo-Json -Compress;exit}
if($p.role -eq 'member' -and $p.action -ne 'pin'){
 $virtual=Get-NetAdapter -IncludeHidden | Where-Object {$_.InterfaceDescription -eq ('VPN Client Adapter - '+$p.nic)}
 if(-not $virtual){throw 'Owned virtual adapter unavailable'}
 if($p.nic -notlike 'LNK*' -and (!$p.nicId -or $virtual.InterfaceGuid.ToString().Trim('{}') -ne $p.nicId.Trim('{}'))){throw 'Virtual adapter ownership changed'}
 $localDns=@((Get-DnsClientServerAddress -InterfaceIndex $physical.ifIndex -AddressFamily IPv4).ServerAddresses)
 if($localDns.Count -eq 0){$localDns=@(Get-DnsClientServerAddress -AddressFamily IPv4 | Where-Object {$_.InterfaceIndex -ne $virtual.ifIndex} | ForEach-Object {$_.ServerAddresses} | Select-Object -Unique)}
 if($localDns.Count -eq 0){throw 'No existing local IPv4 DNS configuration available'}
 if($p.action -eq 'prepare'){
  Set-NetIPInterface -InterfaceIndex $virtual.ifIndex -AddressFamily IPv4 -IgnoreDefaultRoutes Enabled -AutomaticMetric Disabled -InterfaceMetric 5000 -Dhcp Enabled
  Set-DnsClient -InterfaceIndex $virtual.ifIndex -RegisterThisConnectionsAddress $false
  # Empty DNS lists fall back to DHCP on Windows. Reuse local resolvers explicitly.
  Set-DnsClientServerAddress -InterfaceIndex $virtual.ifIndex -ServerAddresses $localDns
  Disable-NetAdapterBinding -Name $virtual.Name -ComponentID ms_tcpip6 | Out-Null
 }
 if($p.action -eq 'check'){
  # IgnoreDefaultRoutes alone does not suppress every IPv4 DHCP route.
  # The freshly created, GUID-verified Link adapter must never provide an internet exit.
  # Windows can label a DHCP-supplied gateway NetMgmt; protocol alone is insufficient.
  Get-NetRoute -InterfaceIndex $virtual.ifIndex -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Remove-NetRoute -Confirm:$false
  Get-NetRoute -InterfaceIndex $virtual.ifIndex | Where-Object {$_.Protocol -eq 'Dhcp' -and $_.NextHop -ne '0.0.0.0' -and $_.DestinationPrefix -notin $p.networks} | Remove-NetRoute -Confirm:$false
  if(@(Get-NetRoute -InterfaceIndex $virtual.ifIndex -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue).Count -gt 0){throw 'Cannot remove default route from Link adapter'}
  $dns=(Get-DnsClientServerAddress -InterfaceIndex $virtual.ifIndex -AddressFamily IPv4).ServerAddresses
  if(($dns -join ',') -ne ($localDns -join ',')){Set-DnsClientServerAddress -InterfaceIndex $virtual.ifIndex -ServerAddresses $localDns}
  if(((Get-DnsClientServerAddress -InterfaceIndex $virtual.ifIndex -AddressFamily IPv4).ServerAddresses -join ',') -ne ($localDns -join ',')){throw 'Cannot retain local DNS on Link adapter'}
  $unexpected=@(Get-NetRoute -InterfaceIndex $virtual.ifIndex | Where-Object {$_.Protocol -eq 'Dhcp' -and $_.NextHop -ne '0.0.0.0' -and $_.DestinationPrefix -notin $p.networks})
  if($unexpected.Count -gt 0){throw 'DHCP supplied an unexpected route'}
  $address=Get-NetIPAddress -InterfaceIndex $virtual.ifIndex -AddressFamily IPv4 | Where-Object {$_.AddressState -eq 'Preferred' -and $_.PrefixOrigin -eq 'Dhcp' -and $_.IPAddress -notlike '169.254.*'} | Select-Object -First 1
  if($address){
   # These routes live only on the identity-checked, journalled Link adapter.
   # Never import a default route or overwrite another interface's route.
   foreach($network in $p.networks){
    if($network -notmatch '^(10\.|172\.(1[6-9]|2[0-9]|3[01])\.|192\.168\.).+/(1[6-9]|2[0-9]|30)$'){throw 'Invalid private destination'}
    $existing=@(Get-NetRoute -InterfaceIndex $virtual.ifIndex -DestinationPrefix $network -ErrorAction SilentlyContinue)
    if($existing.Count -eq 0){New-NetRoute -DestinationPrefix $network -InterfaceIndex $virtual.ifIndex -NextHop $p.remoteGateway -RouteMetric 5 -PolicyStore ActiveStore | Out-Null}
   }
   $lan=Find-NetRoute -RemoteIPAddress $p.remoteGateway -ErrorAction SilentlyContinue | Where-Object {$_.PSObject.Properties['DestinationPrefix']} | Select-Object -First 1
   if(-not $lan -or $lan.InterfaceIndex -ne $virtual.ifIndex){
    @{waiting='lan-route';actualInterface=$lan.InterfaceIndex;expectedInterface=$virtual.ifIndex;prefix=$lan.DestinationPrefix;routeOwned=$ownedRoute} | ConvertTo-Json -Compress;exit
   }
  }
 }
}
@{routeOwned=$ownedRoute} | ConvertTo-Json -Compress
}catch{@{error=$_.Exception.Message} | ConvertTo-Json -Compress;exit 1}
