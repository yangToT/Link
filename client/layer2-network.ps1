param([Parameter(Mandatory=$true)][string]$Request)
$ErrorActionPreference='Stop'
$p=Get-Content -LiteralPath $Request -Raw | ConvertFrom-Json
if($p.account -notmatch '^Link-[A-F0-9]{12}$' -or $p.nic -notmatch '^LNK[A-F0-9]{12}$'){throw 'Invalid resource ownership'}
$physical=Get-NetAdapter -IncludeHidden | Where-Object {$_.InterfaceGuid.ToString().Trim('{}') -eq $p.adapterId.Trim('{}')}
$ip=[Net.IPAddress]::Parse($p.publicServer)
if($ip.AddressFamily -ne 'InterNetwork'){throw 'IPv4 endpoint required'}
$prefix=$ip.ToString()+'/32'
$ownedRoute=[bool]$p.routeOwned
$ownerFile=Join-Path (Split-Path -Parent $Request) 'layer2-route-owner.json'
if($p.action -eq 'cleanup'){
 if(Test-Path -LiteralPath $ownerFile){
  $owner=Get-Content -LiteralPath $ownerFile -Raw | ConvertFrom-Json
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
$overlay=Find-NetRoute -RemoteIPAddress $p.overlayEndpoint | Where-Object {$_.PSObject.Properties['DestinationPrefix']} | Select-Object -First 1
$link=Get-NetAdapter -IncludeHidden | Where-Object {$_.Name -eq 'Link0'}
if(-not $link -or -not $overlay -or $overlay.InterfaceIndex -ne $link.ifIndex){throw 'Private transport is not using the Link adapter'}
if($p.role -eq 'member' -and $p.action -ne 'pin'){
 $virtual=Get-NetAdapter -IncludeHidden | Where-Object {$_.InterfaceDescription -eq ('VPN Client Adapter - '+$p.nic)}
 if(-not $virtual){throw 'Owned virtual adapter unavailable'}
 if($p.action -eq 'prepare'){
  Set-NetIPInterface -InterfaceIndex $virtual.ifIndex -AddressFamily IPv4 -IgnoreDefaultRoutes Enabled -AutomaticMetric Disabled -InterfaceMetric 5000 -Dhcp Enabled
  Set-DnsClient -InterfaceIndex $virtual.ifIndex -RegisterThisConnectionsAddress $false
  & netsh.exe interface ipv4 set dnsservers "name=$($virtual.ifIndex)" source=static address=none validate=no | Out-Null
  if($LASTEXITCODE -ne 0){throw 'Cannot isolate virtual adapter DNS'}
  Disable-NetAdapterBinding -Name $virtual.Name -ComponentID ms_tcpip6 | Out-Null
 }
 if($p.action -eq 'check'){
  if(@(Get-NetRoute -InterfaceIndex $virtual.ifIndex -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue).Count -gt 0){throw 'Virtual adapter attempted to replace internet routing'}
  $dns=(Get-DnsClientServerAddress -InterfaceIndex $virtual.ifIndex -AddressFamily IPv4).ServerAddresses
  if($dns.Count -gt 0){throw 'Virtual adapter attempted to replace DNS'}
  $unexpected=@(Get-NetRoute -InterfaceIndex $virtual.ifIndex | Where-Object {$_.Protocol -eq 'Dhcp' -and $_.NextHop -ne '0.0.0.0' -and $_.DestinationPrefix -notin $p.networks})
  if($unexpected.Count -gt 0){throw 'DHCP supplied an unexpected route'}
  $address=Get-NetIPAddress -InterfaceIndex $virtual.ifIndex -AddressFamily IPv4 | Where-Object {$_.AddressState -eq 'Preferred' -and $_.PrefixOrigin -eq 'Dhcp' -and $_.IPAddress -notlike '169.254.*'} | Select-Object -First 1
  if($address){
   # These routes live only on the randomly named, journalled Link adapter.
   # Never import a default route or overwrite another interface's route.
   foreach($network in $p.networks){
    if($network -notmatch '^(10\.|172\.(1[6-9]|2[0-9]|3[01])\.|192\.168\.).+/(1[6-9]|2[0-9]|30)$'){throw 'Invalid private destination'}
    $existing=@(Get-NetRoute -InterfaceIndex $virtual.ifIndex -DestinationPrefix $network -ErrorAction SilentlyContinue)
    if($existing.Count -eq 0){New-NetRoute -DestinationPrefix $network -InterfaceIndex $virtual.ifIndex -NextHop $p.remoteGateway -RouteMetric 5 -PolicyStore ActiveStore | Out-Null}
   }
   $lan=Find-NetRoute -RemoteIPAddress $p.remoteGateway | Where-Object {$_.PSObject.Properties['DestinationPrefix']} | Select-Object -First 1
   if(-not $lan -or $lan.InterfaceIndex -ne $virtual.ifIndex){throw 'TUN intercepts the remote LAN route'}
  }
 }
}
@{routeOwned=$ownedRoute} | ConvertTo-Json -Compress
