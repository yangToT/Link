using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Link {
internal sealed class AdapterInfo {
 internal string ID="",Name="",Description="",Kind="",IP="",Network="",Gateway="";
 internal int Index;internal bool Physical,Up;
 internal Dictionary<string,object> Json(){return Common.Map("id",ID,"name",Name,"kind",Kind,"ip",IP,"network",Network,"gateway",Gateway,"physical",Physical,"up",Up);}
}
internal static class NetworkDiscovery {
 internal static bool IsTunnel(string name,string description){
  string text=(name+" "+description).ToLowerInvariant();
  return new[]{"singbox","sing-box","sing-tun","wireguard","wintun","v2ray","xray","clash","mihomo","softether","vpn","tap-","tun", "link0"}.Any(text.Contains);
 }
 internal static List<AdapterInfo> Read(){
  var physical=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  // Fail closed when hardware inventory is unavailable; a virtual adapter can also report Ethernet.
  try{using(var query=new ManagementObjectSearcher("SELECT GUID,PhysicalAdapter FROM Win32_NetworkAdapter"))using(var rows=query.Get())foreach(ManagementObject row in rows)using(row){if(row["GUID"]!=null&&row["PhysicalAdapter"] is bool&&(bool)row["PhysicalAdapter"])physical.Add(Convert.ToString(row["GUID"]).Trim('{','}'));}}catch(ManagementException){}catch(UnauthorizedAccessException){}
  var result=new List<AdapterInfo>();
  foreach(var nic in NetworkInterface.GetAllNetworkInterfaces()){
   if(nic.NetworkInterfaceType==NetworkInterfaceType.Loopback)continue;
   try{
    var p=nic.GetIPProperties();var v4=p.GetIPv4Properties();if(v4==null)continue;
    string kind=nic.NetworkInterfaceType==NetworkInterfaceType.Wireless80211?"wifi":"ethernet";
    bool tunnel=IsTunnel(nic.Name,nic.Description);if(tunnel)kind="tunnel";
    bool supported=(nic.NetworkInterfaceType==NetworkInterfaceType.Ethernet||nic.NetworkInterfaceType==NetworkInterfaceType.Wireless80211)&&nic.Description.IndexOf("bluetooth",StringComparison.OrdinalIgnoreCase)<0;
    var address=p.UnicastAddresses.FirstOrDefault(a=>a.Address.AddressFamily==AddressFamily.InterNetwork&&Agent.Private(a.Address));
    var gateway=p.GatewayAddresses.FirstOrDefault(g=>g.Address.AddressFamily==AddressFamily.InterNetwork&&!g.Address.Equals(IPAddress.Any));
    result.Add(new AdapterInfo{ID=nic.Id,Name=nic.Name,Description=nic.Description,Index=v4.Index,Kind=kind,Physical=supported&&physical.Contains(nic.Id.Trim('{','}'))&&!tunnel,Up=nic.OperationalStatus==OperationalStatus.Up,IP=address==null?"":address.Address.ToString(),Network=address==null?"":Prefix(address.Address,address.IPv4Mask),Gateway=gateway==null?"":gateway.Address.ToString()});
   }catch(NetworkInformationException){}
  }
  return result;
 }
 internal static AdapterInfo Select(IEnumerable<AdapterInfo> adapters,string selected){
  var available=adapters.Where(a=>a.Physical&&a.Up&&a.IP!=""&&a.Network!=""&&a.Gateway!="").ToList();
  if(selected!="")return available.SingleOrDefault(a=>a.ID==selected);
  var wired=available.Where(a=>a.Kind=="ethernet").ToList();if(wired.Count>0)return wired.Count==1?wired[0]:null;
  return available.Count==1?available[0]:null;
 }
 internal static Dictionary<string,object> Discover(string selected){return Report(Read(),selected,true);}
 internal static Dictionary<string,object> Report(List<AdapterInfo> adapters,string selected,bool readRoutes){
  var chosen=Select(adapters,selected);var networks=new List<string>();
  if(chosen!=null){networks.Add(chosen.Network);if(readRoutes)networks.AddRange(PrivateRoutes(chosen.Index));}
  var distinct=networks.Distinct().Where(n=>!networks.Any(other=>other!=n&&Contains(other,n))).Take(16).ToArray();
  string warning=chosen==null?(selected!=""?"指定的物理网卡不可用，请检查连接":"无法唯一确定入口网卡，请在设置中选择"):chosen.Kind=="wifi"?"当前使用 Wi-Fi；二层接入需要受支持的有线网卡":"";
  return Common.Map("lanIp",chosen==null?"":chosen.IP,"networks",distinct,"network",Common.Map("adapterId",chosen==null?"":chosen.ID,"adapterName",chosen==null?"":chosen.Name,"kind",chosen==null?"":chosen.Kind,"gateway",chosen==null?"":chosen.Gateway,"bridgeEligible",chosen!=null&&chosen.Kind=="ethernet","tunDetected",adapters.Any(a=>a.Up&&a.Kind=="tunnel"&&!a.Name.Equals("Link0",StringComparison.OrdinalIgnoreCase)&&!a.Description.ToLowerInvariant().Contains("wireguard")),"warning",warning,"adapters",adapters.Where(a=>a.Physical).Select(a=>a.Json()).ToArray()));
 }
 static string Prefix(IPAddress ip,IPAddress mask){var b=ip.GetAddressBytes();var m=mask.GetAddressBytes();int bits=0;bool zero=false;for(int i=0;i<32;i++){bool bit=(m[i/8]&(1<<(7-i%8)))!=0;if(bit&&zero)return "";if(bit)bits++;else zero=true;}if(bits<16||bits>30)return "";for(int i=0;i<4;i++)b[i]&=m[i];return new IPAddress(b)+"/"+bits;}
 internal static bool Contains(string prefix,string other){var parts=prefix.Split('/');var target=other.Split('/');int bits=Int32.Parse(parts[1]);if(bits>Int32.Parse(target[1]))return false;var a=IPAddress.Parse(parts[0]).GetAddressBytes();var b=IPAddress.Parse(target[0]).GetAddressBytes();for(int i=0;i<bits;i++)if((a[i/8]&(1<<(7-i%8)))!=(b[i/8]&(1<<(7-i%8))))return false;return true;}
 [DllImport("iphlpapi.dll")]static extern int GetIpForwardTable(IntPtr table,ref int size,bool order);
 static IEnumerable<string> PrivateRoutes(int selectedIndex){
  var result=new List<string>();int size=0;GetIpForwardTable(IntPtr.Zero,ref size,false);if(size<4||size>1048576)return result;var buffer=Marshal.AllocHGlobal(size);
  try{if(GetIpForwardTable(buffer,ref size,false)!=0)return result;int count=Marshal.ReadInt32(buffer);for(int i=0;i<count&&4+(i+1)*56<=size;i++){var row=IntPtr.Add(buffer,4+i*56);if(Marshal.ReadInt32(row,16)!=selectedIndex)continue;var dest=new IPAddress(BitConverter.GetBytes(Marshal.ReadInt32(row)));var mask=new IPAddress(BitConverter.GetBytes(Marshal.ReadInt32(row,4)));string prefix=Prefix(dest,mask);if(Agent.Private(dest)&&prefix!="")result.Add(prefix);}}finally{Marshal.FreeHGlobal(buffer);}return result;
 }
 internal static void Test(){
  var tun=new AdapterInfo{ID="tun",Name="singbox_tun",Kind="tunnel",Physical=false,Up=true,IP="172.18.0.1",Network="172.18.0.0/30",Gateway="172.18.0.2"};
  var lan=new AdapterInfo{ID="lan",Name="Ethernet",Kind="ethernet",Physical=true,Up=true,IP="192.168.30.10",Network="192.168.30.0/24",Gateway="192.168.30.1"};
  var list=new List<AdapterInfo>{tun,lan};if(Select(list,"")!=lan||Select(list,"tun")!=null)throw new Exception("TUN chosen as physical entry");
  var second=new AdapterInfo{ID="second",Kind="ethernet",Physical=true,Up=true,IP="10.1.1.2",Network="10.1.1.0/24",Gateway="10.1.1.1"};list.Add(second);if(Select(list,"")!=null||Select(list,"lan")!=lan)throw new Exception("Ambiguous adapter silently selected");
  lan.Up=false;if(Select(list,"lan")!=null)throw new Exception("Offline adapter retained");lan.Up=true;lan.Kind="wifi";var report=Report(new List<AdapterInfo>{tun,lan},"",false);if(Common.Bool(Common.Obj(report,"network"),"bridgeEligible"))throw new Exception("Wi-Fi bridge accepted");
  if(!IsTunnel("renamed","sing-tun Tunnel")||!IsTunnel("renamed","WireGuard Tunnel"))throw new Exception("Virtual adapter filter failed");
 }
}
}
