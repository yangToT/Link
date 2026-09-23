# Prepare audited, extracted official components. Does not create a NIC or bridge.
param([string]$ComponentSource,[string]$Report,[switch]$TestHash)
$ErrorActionPreference='Stop'
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Diagnostics;
public static class LinkSoftEtherPassword {
 public static void Command(string exe,string args){using(var p=new Process()){p.StartInfo=new ProcessStartInfo(exe,args){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};p.Start();p.StandardInput.Close();var output=p.StandardOutput.ReadToEndAsync();var error=p.StandardError.ReadToEndAsync();if(!p.WaitForExit(15000)){p.Kill();p.WaitForExit();throw new InvalidOperationException("Component management command timed out");}if(p.ExitCode!=0)throw new InvalidOperationException("Component management command failed (exit "+p.ExitCode+")");}}
 static uint R(uint x,int n){return (x<<n)|(x>>(32-n));}
 public static byte[] Hash(string password){
  byte[] value=Encoding.UTF8.GetBytes(password);int length=((value.Length+9+63)/64)*64;byte[] data=new byte[length];Array.Copy(value,data,value.Length);data[value.Length]=128;ulong bits=(ulong)value.Length*8;
  for(int i=0;i<8;i++)data[length-1-i]=(byte)(bits>>(8*i));
  uint[] h={0x67452301,0xefcdab89,0x98badcfe,0x10325476,0xc3d2e1f0};
  for(int offset=0;offset<length;offset+=64){uint[] w=new uint[80];for(int i=0;i<16;i++){int p=offset+i*4;w[i]=((uint)data[p]<<24)|((uint)data[p+1]<<16)|((uint)data[p+2]<<8)|data[p+3];}for(int i=16;i<80;i++)w[i]=w[i-3]^w[i-8]^w[i-14]^w[i-16];
   uint a=h[0],b=h[1],c=h[2],d=h[3],e=h[4];unchecked{for(int i=0;i<80;i++){uint f,k;if(i<20){f=(b&c)|(~b&d);k=0x5a827999;}else if(i<40){f=b^c^d;k=0x6ed9eba1;}else if(i<60){f=(b&c)|(b&d)|(c&d);k=0x8f1bbcdc;}else{f=b^c^d;k=0xca62c1d6;}uint t=R(a,5)+f+e+k+w[i];e=d;d=c;c=R(b,30);b=a;a=t;}h[0]+=a;h[1]+=b;h[2]+=c;h[3]+=d;h[4]+=e;}
  }
  byte[] result=new byte[20];for(int i=0;i<5;i++)for(int j=0;j<4;j++)result[i*4+j]=(byte)(h[i]>>(24-j*8));Array.Clear(value,0,value.Length);Array.Clear(data,0,data.Length);return result;
 }
}
'@
if($TestHash){
 foreach($v in @(@('','F96CEA198AD1DD5617AC084A3D92C6107708C0EF'),@('abc','0164B8A914CD2A5E74C4F7FF082C4D97F1EDF880'))){if([BitConverter]::ToString([LinkSoftEtherPassword]::Hash($v[0])).Replace('-','') -ne $v[1]){throw 'Legacy hash mismatch'}}
 'PASS: SoftEther configuration password format';return
}
function Log([string]$text){if($Report){$text | Add-Content -LiteralPath $Report};Write-Output $text}
function Baseline {
 @{
  routes=@(Get-NetRoute | Where-Object DestinationPrefix -in @('0.0.0.0/0','::/0') | Select-Object InterfaceIndex,DestinationPrefix,NextHop,RouteMetric | Sort-Object InterfaceIndex,DestinationPrefix)
  dns=@(Get-DnsClientServerAddress | Where-Object InterfaceAlias -ne 'Link0' | Select-Object InterfaceIndex,AddressFamily,ServerAddresses | Sort-Object InterfaceIndex,AddressFamily)
  tun=@(Get-NetAdapter -IncludeHidden | Where-Object Name -Match 'tun|sing|v2ray' | Select-Object Name,Status,InterfaceGuid | Sort-Object Name)
 } | ConvertTo-Json -Depth 6 -Compress
}
$folder=Join-Path $env:ProgramFiles 'Link\softether'
$data=Join-Path $env:ProgramData 'Link'
$created=@();$rules=@()
try {
 if($Report){'' | Set-Content -LiteralPath $Report}
 if(-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Run as administrator'}
 foreach($name in @('SEVPNCLIENT','SEVPNBRIDGE','SEVPNCLIENTDEV','SEVPNBRIDGEDEV')){if(Get-Service -Name $name -ErrorAction SilentlyContinue){throw 'Existing SoftEther service requires separate ownership review'}}
 if(Test-Path -LiteralPath $folder){throw 'Dedicated component folder already exists'}
 if(Test-Path -LiteralPath (Join-Path $data 'layer2-local.bin')){throw 'Existing local component credentials must be retained'}
 if(Get-NetTCPConnection -State Listen | Where-Object LocalPort -in @(5555,9930)){throw 'A local management port is already occupied'}
 foreach($role in @('bridge','client')){foreach($name in @('vpncmd.exe',('vpn'+$role+'.exe'))){$exe=Join-Path $ComponentSource ($role+'-portable\'+$name);$sig=Get-AuthenticodeSignature -LiteralPath $exe;if($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'SOFTETHER CORPORATION'){throw 'Component signature invalid'}}}
 $before=Baseline
 $drivers=@(Get-CimInstance Win32_SystemDriver | Where-Object Name -Match '^(SeLow|See|NPF)$' | Select-Object Name,PathName)
 $packages=@(Get-WindowsDriver -Online -All | Where-Object {$_.OriginalFileName -match '[\\/]selow[^\\/]*\.inf$'} | Select-Object Driver,OriginalFileName,ProviderName,ClassName,Version)
 $owned=@{program=$folder;version='4.44-9807';driversBefore=$drivers;driverPackagesBefore=$packages;phase='preparing';networkBefore=$before;services=@();firewall=@()}
 $journal=Join-Path $data 'layer2-install.json'
 $owned | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $journal -Encoding UTF8
 New-Item -ItemType Directory -Path $folder | Out-Null
 # Credentials and component configuration remain SYSTEM/Administrators-only.
 $acl=New-Object Security.AccessControl.DirectorySecurity
 $acl.SetAccessRuleProtection($true,$false)
 foreach($sid in @('S-1-5-18','S-1-5-32-544')){$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule ([Security.Principal.SecurityIdentifier]$sid),'FullControl','ContainerInherit,ObjectInherit','None','Allow'))}
 Set-Acl -LiteralPath $folder -AclObject $acl
 foreach($role in @('bridge','client')){Copy-Item -LiteralPath (Join-Path $ComponentSource ($role+'-portable')) -Destination (Join-Path $folder $role) -Recurse}
 foreach($name in @('vpncmd.exe','hamcore.se2','lang.config')){Copy-Item -LiteralPath (Join-Path $folder ('client\'+$name)) -Destination (Join-Path $folder $name)}
 $rng=[Security.Cryptography.RandomNumberGenerator]::Create();$bytes=New-Object byte[] 32
 $rng.GetBytes($bytes);$clientPassword=[BitConverter]::ToString($bytes).Replace('-','')
 $rng.GetBytes($bytes);$bridgePassword=[BitConverter]::ToString($bytes).Replace('-','');$rng.Dispose();[Array]::Clear($bytes,0,$bytes.Length)
 $clientHash=[Convert]::ToBase64String([LinkSoftEtherPassword]::Hash($clientPassword))
 $bridgeHash=[Convert]::ToBase64String([LinkSoftEtherPassword]::Hash($bridgePassword))
 @"
declare root
{
 byte EncryptedPassword $clientHash
 bool PasswordRemoteOnly false
 declare Config
 {
  bool AllowRemoteConfig false
  bool DisableRpcDynamicPortListener true
  bool UseKeepConnect false
  bool NoChangeWcmNetworkSettingOnWindows8 true
 }
 declare AccountDatabase
 {
 }
}
"@ | Set-Content -LiteralPath (Join-Path $folder 'client\vpn_client.config') -Encoding ASCII
 @"
declare root
{
 declare ServerConfiguration
 {
  byte HashedPassword $bridgeHash
  bool NoHighPriorityProcess true
  bool DisableNatTraversal true
  bool DisableSSTPServer true
  bool DisableOpenVPNServer true
  bool DisableJsonRpcWebApi true
  bool UseKeepConnect false
 }
 declare ListenerList
 {
  declare Listener0
  {
   bool Enabled true
   uint Port 5555
  }
 }
 declare VirtualHUB
 {
 }
}
"@ | Set-Content -LiteralPath (Join-Path $folder 'bridge\vpn_bridge.config') -Encoding ASCII
 foreach($role in @('client','bridge')){
  $exe=Join-Path $folder ($role+'\vpn'+$role+'.exe');$rule='Link-Layer2-'+$role
  New-NetFirewallRule -Name $rule -DisplayName $rule -Direction Inbound -Action Block -Program $exe -RemoteAddress @('0.0.0.0-126.255.255.255','128.0.0.0-255.255.255.255','::2-ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff') -Profile Any | Out-Null
  $rules+=$rule;$name=if($role -eq 'client'){'SEVPNCLIENT'}else{'SEVPNBRIDGE'}
  New-Service -Name $name -BinaryPathName ('"'+$exe+'" /service') -DisplayName ('Link Layer2 '+$role) -StartupType Automatic | Out-Null
  $created+=$name;$owned.services=$created;$owned.firewall=$rules;$owned | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $journal -Encoding UTF8
  Start-Service $name;(Get-Service $name).WaitForStatus('Running',[TimeSpan]::FromSeconds(30))
 }
 $cmd=Join-Path $folder 'vpncmd.exe'
 & (Join-Path $PSScriptRoot 'prepare-layer2.ps1') -VpnCmd $cmd -ClientPassword (ConvertTo-SecureString $clientPassword -AsPlainText -Force) -BridgePassword (ConvertTo-SecureString $bridgePassword -AsPlainText -Force)
 foreach($role in @('client','bridge')){
  $secret=if($role -eq 'client'){$clientPassword}else{$bridgePassword}
  $command=if($role -eq 'client'){'AccountList'}else{'CascadeList'}
  $arguments=if($role -eq 'client'){@('127.0.0.1','/CLIENT','/PROGRAMMING',('/PASSWORD:'+$secret),'/CMD',$command)}else{@('127.0.0.1:5555','/SERVER','/ADMINHUB:BRIDGE','/PROGRAMMING',('/PASSWORD:'+$secret),'/CMD',$command)}
  [LinkSoftEtherPassword]::Command($cmd,($arguments -join ' '))
  Log ($role+' component authenticated; no connection created')
 }
 $after=Baseline;$owned.networkAfter=$after;$owned.phase='ready'
 $owned.driversAfter=@(Get-CimInstance Win32_SystemDriver | Where-Object Name -Match '^(SeLow|See|NPF)$' | Select-Object Name,PathName)
 $owned.driverPackagesAfter=@(Get-WindowsDriver -Online -All | Where-Object {$_.OriginalFileName -match '[\\/]selow[^\\/]*\.inf$'} | Select-Object Driver,OriginalFileName,ProviderName,ClassName,Version)
 $owned | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $journal -Encoding UTF8
 if($before -ne $after){throw 'Network baseline differs; review saved state before enabling layer2'}
 Log 'Default routes, DNS and TUN adapter state unchanged'
 Log 'Client and bridge prepared; no virtual adapter or physical bridge created; current connection mode retained'
 Log 'SUCCESS'
}catch{
 if($owned -and $journal){
  $owned.phase='failed'
  $owned.driversAfter=@(Get-CimInstance Win32_SystemDriver | Where-Object Name -Match '^(SeLow|See|NPF)$' | Select-Object Name,PathName)
  $owned.driverPackagesAfter=@(Get-WindowsDriver -Online -All | Where-Object {$_.OriginalFileName -match '[\\/]selow[^\\/]*\.inf$'} | Select-Object Driver,OriginalFileName,ProviderName,ClassName,Version)
  $owned | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $journal -Encoding UTF8
 }
 foreach($name in $created){Stop-Service $name -ErrorAction SilentlyContinue;Set-Service $name -StartupType Manual -ErrorAction SilentlyContinue}
 Log ('FAILED: '+$_.Exception.Message);throw
}finally{$clientPassword=$null;$bridgePassword=$null}
