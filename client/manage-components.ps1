param([ValidateSet('install','repair','remove','extract-test')][string]$Action,[string]$Source)
$ErrorActionPreference='Stop'
$root=Join-Path $env:ProgramFiles 'Link\softether'
$data=Join-Path $env:ProgramData 'Link'
if($Action -ne 'extract-test'){
 if(-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Run as administrator'}
 $report=Join-Path $data 'component-install.log'
 'Preparing optional components' | Set-Content -LiteralPath $report
}
function Log($text){if($report){$text | Add-Content -LiteralPath $report};Write-Output $text}
# Load signed installer resources as data; never execute the downloaded installer.
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
public static class LinkPayload {
 [DllImport("kernel32",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr LoadLibraryEx(string name,IntPtr file,uint flags);
 [DllImport("kernel32",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr FindResource(IntPtr module,string name,string type);
 [DllImport("kernel32")]static extern uint SizeofResource(IntPtr module,IntPtr resource);
 [DllImport("kernel32")]static extern IntPtr LoadResource(IntPtr module,IntPtr resource);
 [DllImport("kernel32")]static extern IntPtr LockResource(IntPtr resource);
 [DllImport("kernel32")]static extern bool FreeLibrary(IntPtr module);
 public static byte[] Read(string file,string name,bool compressed){
  var module=LoadLibraryEx(file,IntPtr.Zero,2);if(module==IntPtr.Zero)throw new IOException("Cannot load component resources");
  try{var resource=FindResource(module,name,"DATAFILE");uint size=SizeofResource(module,resource);if(resource==IntPtr.Zero||size==0||size>100*1024*1024)throw new IOException("Invalid component resource");
   var pointer=LockResource(LoadResource(module,resource));if(pointer==IntPtr.Zero)throw new IOException("Missing component resource");var bytes=new byte[size];Marshal.Copy(pointer,bytes,0,bytes.Length);if(!compressed)return bytes;
   if(bytes.Length<10||bytes[4]!=0x78||(((int)bytes[4]*256+bytes[5])%31)!=0||(bytes[5]&32)!=0)throw new IOException("Invalid compressed payload");
   uint length=((uint)bytes[0]<<24)|((uint)bytes[1]<<16)|((uint)bytes[2]<<8)|bytes[3];if(length<2||length>100*1024*1024)throw new IOException("Invalid payload length");
   using(var source=new MemoryStream(bytes,6,bytes.Length-10))using(var deflate=new DeflateStream(source,CompressionMode.Decompress))using(var output=new MemoryStream()){
    var buffer=new byte[65536];int n;while((n=deflate.Read(buffer,0,buffer.Length))>0){if(output.Length+n>length)throw new IOException("Oversize payload");output.Write(buffer,0,n);}var result=output.ToArray();if(result.Length!=length||result[0]!=77||result[1]!=90)throw new IOException("Invalid executable payload");return result;
   }
  }finally{FreeLibrary(module);}
 }
}
'@
try{
 if($Action -eq 'remove'){
  & (Join-Path $PSScriptRoot 'uninstall.ps1') -Layer2Only
  if($LASTEXITCODE -ne 0){throw 'Component removal failed'}
  Log 'SUCCESS: components removed';return
 }
 $cache=if($Action -eq 'extract-test'){$Source}else{Join-Path $data 'component-downloads'}
 if(-not $cache){throw 'Extraction test source is required'}
 New-Item -ItemType Directory -Path $cache -Force | Out-Null
 $packages=@(
  @{role='client';name='softether-vpnclient-v4.44-9807-rtm-2025.04.16-windows-x86_x64-intel.exe';hash='ab006331f1f646bc319585088239d55bc54ecde5c269009268e9598307da7a44'},
  @{role='bridge';name='softether-vpnserver_vpnbridge-v4.44-9807-rtm-2025.04.16-windows-x86_x64-intel.exe';hash='59831707141858a55b624e044bd5ee49a2f1aca421f03f98ed1fae30b525d792'}
 )
 [Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12
 foreach($package in $packages){
  $file=Join-Path $cache $package.name
  if(-not (Test-Path -LiteralPath $file) -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $package.hash){
   if($Action -eq 'extract-test'){throw 'Test package not present'}
   Log ('Downloading verified '+$package.role+' component')
   $partial=$file+'.partial'
   Invoke-WebRequest -UseBasicParsing -Uri ('https://github.com/SoftEtherVPN/SoftEtherVPN_Stable/releases/download/v4.44-9807-rtm/'+$package.name) -OutFile $partial -TimeoutSec 180
   if((Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ne $package.hash){throw 'Downloaded component checksum mismatch'}
   Move-Item -LiteralPath $partial -Destination $file -Force
  }
  if((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $package.hash){throw 'Cached component checksum mismatch'}
  $signature=Get-AuthenticodeSignature -LiteralPath $file
  if($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'SOFTETHER CORPORATION'){throw 'Component publisher signature invalid'}
  $output=Join-Path $cache ($package.role+'-portable');New-Item -ItemType Directory -Path $output -Force | Out-Null
  foreach($name in @('vpncmd',('vpn'+$package.role))){
   $target=Join-Path $output ($name+'.exe')
   [IO.File]::WriteAllBytes($target,[LinkPayload]::Read($file,($name.ToUpperInvariant()+'_X64.EXE'),$true))
   $sig=Get-AuthenticodeSignature -LiteralPath $target
   if($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'SOFTETHER CORPORATION'){throw 'Extracted executable signature invalid'}
  }
  [IO.File]::WriteAllBytes((Join-Path $output 'hamcore.se2'),[LinkPayload]::Read($file,'RAW_HAMCORE.SE2',$false))
  [IO.File]::WriteAllText((Join-Path $output 'lang.config'),"en`n")
 }
 if($Action -eq 'extract-test'){Log 'PASS: pinned installer hashes, publisher signatures, bounded resource extraction and signed payloads';return}
 if($Action -eq 'repair' -and (Test-Path -LiteralPath $root)){& (Join-Path $PSScriptRoot 'uninstall.ps1') -Layer2Only -KeepComponentCache}
 & (Join-Path $PSScriptRoot 'install-layer2.ps1') -ComponentSource $cache -Report $report
 if($LASTEXITCODE -ne 0){throw 'Component preparation failed'}
 foreach($name in @('SEVPNCLIENT','SEVPNBRIDGE')){Set-Service $name -StartupType Manual;Stop-Service $name}
 Log 'SUCCESS: installed; feature remains disabled'
}catch{Log ('FAILED: '+$_.Exception.Message);exit 1}
