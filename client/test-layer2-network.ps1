# Runs the embedded guard with in-memory cmdlet doubles. No system network changes.
$ErrorActionPreference='Stop'
$testDir=Join-Path ([IO.Path]::GetTempPath()) ('Link-Guard-Test-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testDir | Out-Null
$global:LinkGuardTestroutes=@();$global:LinkGuardTestremoved=@();$global:LinkGuardTestintercept=$false
function Get-NetAdapter { param([switch]$IncludeHidden)
 [pscustomobject]@{InterfaceGuid=[guid]'11111111-1111-1111-1111-111111111111';ifIndex=6;HardwareInterface=$true;Status='Up';PhysicalMediaType='802.3';Name='Ethernet'}
 [pscustomobject]@{InterfaceGuid=[guid]'22222222-2222-2222-2222-222222222222';ifIndex=9;HardwareInterface=$false;Status='Up';Name='Link0'}
}
function Get-NetRoute { param($DestinationPrefix,$InterfaceIndex,$ErrorAction)
 $global:LinkGuardTestroutes | Where-Object {($_.DestinationPrefix -eq $DestinationPrefix) -and (-not $InterfaceIndex -or $_.InterfaceIndex -eq $InterfaceIndex)}
}
function New-NetRoute { param($DestinationPrefix,$InterfaceIndex,$NextHop,$RouteMetric,$PolicyStore)
 $global:LinkGuardTestroutes+= [pscustomobject]@{DestinationPrefix=$DestinationPrefix;InterfaceIndex=$InterfaceIndex;NextHop=$NextHop;RouteMetric=$RouteMetric;Protocol='NetMgmt'}
}
function Remove-NetRoute { param([Parameter(ValueFromPipeline=$true)]$InputObject,[switch]$Confirm)
 process {$global:LinkGuardTestremoved+=$InputObject;$global:LinkGuardTestroutes=@($global:LinkGuardTestroutes | Where-Object {$_ -ne $InputObject})}
}
function Find-NetRoute {param($RemoteIPAddress)
 [pscustomobject]@{DestinationPrefix='0.0.0.0/0';InterfaceIndex=$(if($global:LinkGuardTestintercept){99}elseif($RemoteIPAddress -eq '100.88.0.1'){9}else{6})}
}
try {
 $p=@{account='Link-ABCDEF012345';nic='LNKABCDEF012345';adapterId='{11111111-1111-1111-1111-111111111111}';publicServer='203.0.113.1';overlayEndpoint='100.88.0.1';gateway='192.168.30.1';role='member';action='pin'}
 $request=Join-Path $testDir 'request.json'
 $guard=Join-Path $PSScriptRoot 'layer2-network.ps1'
 function Invoke-Guard {$p | ConvertTo-Json | Set-Content -LiteralPath $request -Encoding UTF8; & $guard -Request $request}
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
 $global:LinkGuardTestroutes=@();$p.action='pin';$global:LinkGuardTestintercept=$true;$rejected=$false
 try {$null=Invoke-Guard} catch {$rejected=$true}
 if(-not $rejected -or -not (Test-Path (Join-Path $testDir 'layer2-route-owner.json'))){throw 'Interception or recovery record not detected'}
 $p.action='cleanup';$null=Invoke-Guard;if($global:LinkGuardTestroutes.Count -ne 0){throw 'Failed pin left route behind'}
 Write-Output 'PASS: pin before adapter creation, TUN interception, ownership-only cleanup, failed-pin recovery, idempotent cleanup'
} finally {
 Remove-Variable -Scope Global -Name LinkGuardTestroutes,LinkGuardTestremoved,LinkGuardTestintercept -ErrorAction SilentlyContinue
 # Only this test's generated temp directory is removed.
 if([IO.Path]::GetFullPath($testDir).StartsWith([IO.Path]::GetTempPath(),[StringComparison]::OrdinalIgnoreCase)){Remove-Item -LiteralPath $testDir -Recurse -Force}
}
