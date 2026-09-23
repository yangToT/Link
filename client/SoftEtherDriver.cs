using System;
using System.IO;
using System.IO.Compression;
using System.Net.NetworkInformation;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Link {
internal static class SoftEtherDriver {
 static uint U32(BinaryReader input){var b=input.ReadBytes(4);if(b.Length!=4)throw new InvalidDataException();return ((uint)b[0]<<24)|((uint)b[1]<<16)|((uint)b[2]<<8)|b[3];}
 internal static byte[] Extract(string hamcore){
  using(var stream=File.OpenRead(hamcore))using(var input=new BinaryReader(stream)){
   if(Encoding.ASCII.GetString(input.ReadBytes(7))!="HamCore")throw new InvalidDataException("组件资源头无效");uint count=U32(input);if(count>100000)throw new InvalidDataException();
   for(uint i=0;i<count;i++){
    uint length=U32(input);if(length<1||length>2048)throw new InvalidDataException();string name=Encoding.ASCII.GetString(input.ReadBytes((int)length-1));uint size=U32(input),packed=U32(input),offset=U32(input);
    if(name!="driver_installer_x64.exe")continue;
    if(size<2||size>32*1024*1024||packed<6||packed>32*1024*1024||(long)offset+packed>stream.Length)throw new InvalidDataException();
    stream.Position=offset;byte[] bytes=input.ReadBytes((int)packed);if(bytes[0]!=0x78||(((int)bytes[0]*256+bytes[1])%31)!=0||(bytes[1]&32)!=0)throw new InvalidDataException();
    using(var compressed=new MemoryStream(bytes,2,bytes.Length-6))using(var deflate=new DeflateStream(compressed,CompressionMode.Decompress))using(var output=new MemoryStream()){
     var buffer=new byte[65536];int n;while((n=deflate.Read(buffer,0,buffer.Length))>0){if(output.Length+n>size)throw new InvalidDataException();output.Write(buffer,0,n);}byte[] result=output.ToArray();if(result.Length!=size||result[0]!=77||result[1]!=90)throw new InvalidDataException();return result;
    }
   }
  }throw new InvalidOperationException("组件缺少签名驱动安装器，请修复组件");
 }
 internal static string Create(string nic){Prepare(nic);return CreatePrepared(nic);}
 internal static void Prepare(string nic){
  if(!Regex.IsMatch(nic,@"^VPN(?:[2-9]|[1-9][0-9]|1[01][0-9]|12[0-7])?$"))throw new CommandFailure(32);
  if(NetworkInterface.GetAllNetworkInterfaces().Any(n=>n.Description.EndsWith(" - "+nic,StringComparison.OrdinalIgnoreCase)))throw new CommandFailure(30);
  // vpncmd NicCreate delegates to an interactive notification helper on Windows.
  // Services have no such helper. Invoke the same signed upstream installer directly,
  // without opening a privileged local notification listener.
  string work=Path.Combine(Common.Home,"driver-work");Directory.CreateDirectory(work);
  string hamcore=Path.Combine(Components.Root,"client","hamcore.se2"),exe=Path.Combine(work,"driver_installer.exe");
  File.WriteAllBytes(exe,Extract(hamcore));File.Copy(hamcore,Path.Combine(work,"hamcore.se2"),true);File.WriteAllText(Path.Combine(work,"lang.config"),"en\n");
  string script=Path.Combine(work,"verify.ps1");
  File.WriteAllText(script,"$ErrorActionPreference='Stop'\r\n$s=Get-AuthenticodeSignature -LiteralPath (Join-Path $PSScriptRoot 'driver_installer.exe')\r\nif($s.Status -ne 'Valid' -or $s.SignerCertificate.Subject -notmatch 'SOFTETHER CORPORATION'){throw 'Driver installer signature invalid'}",new UTF8Encoding(true));
  Common.Run(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell\\v1.0\\powershell.exe"),"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "+Common.Quote(script),30000);
 }
 internal static string CreatePrepared(string nic){
  if(!Regex.IsMatch(nic,@"^VPN(?:[2-9]|[1-9][0-9]|1[01][0-9]|12[0-7])?$"))throw new CommandFailure(32);
  if(NetworkInterface.GetAllNetworkInterfaces().Any(n=>n.Description.EndsWith(" - "+nic,StringComparison.OrdinalIgnoreCase)))throw new CommandFailure(30);
  return Common.Run(Path.Combine(Common.Home,"driver-work","driver_installer.exe"),"instvlan "+nic,120000);
 }
}
}
