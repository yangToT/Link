using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Link {
// An optional, explicitly prepared SoftEther installation supplies the signed adapter driver.
// Only journalled Link accounts, bridges and virtual adapters are ever modified.
internal sealed class Layer2 {
 readonly string journal;
 readonly Func<Dictionary<string,object>> loadConfig;
 readonly Func<Dictionary<string,object>,bool,string,bool,string> command;
 readonly Func<Dictionary<string,object>,string> shell;
 Dictionary<string,object> owned;string signature="";
 internal Dictionary<string,object> Status=Common.Map("state","off","message","未启用局域网接入","ip","");
 internal Layer2():this(Common.Home,LocalConfig,ExecuteCli,Shell){}
 internal Layer2(string directory,Func<Dictionary<string,object>> config,Func<Dictionary<string,object>,bool,string,bool,string> cli,Func<Dictionary<string,object>,string> guard){journal=Path.Combine(directory,"layer2-journal.json");loadConfig=config;command=cli;shell=guard;if(File.Exists(journal))owned=Common.Parse(File.ReadAllText(journal));}
 string Cli(Dictionary<string,object> config,bool entry,string text,bool cleanup=false){return command(config,entry,text,cleanup);}
 internal static string Atom(string s){if(!Regex.IsMatch(s??"","^[A-Za-z0-9_-]{1,80}$"))throw new InvalidOperationException("二层组件参数无效");return s;}
 internal static Dictionary<string,object> LocalConfig(){
  string path=Path.Combine(Common.Home,"layer2-local.bin");if(!File.Exists(path))throw new InvalidOperationException("尚未安装和配置二层接入组件");
  return Common.Parse(Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path),null,DataProtectionScope.LocalMachine)));
 }
 void Save(){string tmp=journal+".tmp";File.WriteAllText(tmp,Common.Json(owned));if(File.Exists(journal))File.Replace(tmp,journal,null);else File.Move(tmp,journal);}
 static string ExecuteCli(Dictionary<string,object> config,bool entry,string command,bool cleanup){
  string exe=Common.Text(config,"vpncmd");if(!Path.IsPathRooted(exe)||!File.Exists(exe)||Path.GetFileName(exe).IndexOf("vpncmd",StringComparison.OrdinalIgnoreCase)<0)throw new InvalidOperationException("二层管理组件未安装");
  string componentRoot=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"Link","softether")+Path.DirectorySeparatorChar;
  if(!Path.GetFullPath(exe).StartsWith(componentRoot,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("二层组件必须安装在 Link 专属目录");
  string service=entry?"SEVPNBRIDGE":"SEVPNCLIENT";int installed=0;
  foreach(string variant in new[]{service,service+"DEV"})using(var key=Microsoft.Win32.Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Services\\"+variant)){if(key==null)continue;installed++;string path=Convert.ToString(key.GetValue("ImagePath"));if(!path.TrimStart('"').StartsWith(componentRoot,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("检测到未归 Link 管理的二层组件，拒绝更改");}
  if(installed!=1)throw new InvalidOperationException("二层组件缺失或存在多个版本，无法确定管理对象");
  string password=Common.Text(config,entry?"bridgePassword":"clientPassword");if(password.Length<20)throw new InvalidOperationException("二层本机管理凭据尚未配置");
  string input=Path.Combine(Common.Home,"layer2-command-"+Guid.NewGuid().ToString("N")+".txt");
  // Device credentials go in this SYSTEM/Administrators-only temporary input, never command arguments.
  File.WriteAllText(input,command+"\r\nexit\r\n",new UTF8Encoding(true));
  try{return Common.Run(exe,"127.0.0.1"+(entry?":5555 /SERVER":" /CLIENT")+" /PASSWORD:"+Common.Quote(password)+(entry?" /HUB:BRIDGE":"")+" /IN:"+Common.Quote(input),12000,cleanup?new[]{29,36,37,61,76}:new int[0]);}finally{File.Delete(input);}
 }
 static string Shell(Dictionary<string,object> payload){
  string request=Path.Combine(Common.Home,"layer2-request-"+Guid.NewGuid().ToString("N")+".json");
  string script=Path.Combine(Common.Home,"layer2-network.ps1");
  using(var input=System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("Link.Layer2Network"))using(var target=File.Create(script))input.CopyTo(target);
  File.WriteAllText(request,Common.Json(payload));
  try{return Common.Run(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell\\v1.0\\powershell.exe"),"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "+Common.Quote(script)+" -Request "+Common.Quote(request),12000);}finally{File.Delete(request);}
 }
 internal static void ValidatePlan(Dictionary<string,object> plan,Dictionary<string,object> local){
  Atom(Common.Text(plan,"hub"));Atom(Common.Text(plan,"username"));Atom(Common.Text(plan,"password"));
  Uri endpoint;IPAddress ip;
  if(!Uri.TryCreate("https://"+Common.Text(plan,"endpoint"),UriKind.Absolute,out endpoint)||!IPAddress.TryParse(endpoint.Host,out ip)||ip.AddressFamily!=System.Net.Sockets.AddressFamily.InterNetwork||ip.GetAddressBytes()[0]!=100||ip.GetAddressBytes()[1]<64||ip.GetAddressBytes()[1]>127||endpoint.AbsolutePath!="/"||endpoint.UserInfo!=""||endpoint.Query!=""||endpoint.Fragment!="")throw new InvalidOperationException("二层端点必须位于专用网络");
  var network=Common.Obj(local,"network");string role=Common.Text(plan,"role");
  if(role!="entry"&&role!="member")throw new InvalidOperationException("二层角色无效");
  if(role=="entry"&&!Common.Bool(network,"bridgeEligible"))throw new InvalidOperationException("当前网卡不支持二层入口，请连接指定有线网卡");
  if(Common.Text(network,"adapterId")=="")throw new InvalidOperationException("无法确定本机物理网络，请先选择网卡");
  if(role=="member"){
   foreach(var prefix in Strings(local,"networks"))foreach(var remote in Strings(plan,"networks"))if(NetworkDiscovery.Contains(prefix,remote)||NetworkDiscovery.Contains(remote,prefix))throw new InvalidOperationException("两端网段重叠，二层接入未启动");
  }
  Common.ReadCertificate(Encoding.ASCII.GetBytes(Common.Text(plan,"certificate")));
 }
 static IEnumerable<string> Strings(Dictionary<string,object> d,string key){object value;if(!d.TryGetValue(key,out value)||!(value is System.Collections.IEnumerable))return new string[0];return ((System.Collections.IEnumerable)value).Cast<object>().Select(Convert.ToString);}
 internal void Apply(Dictionary<string,object> plan,Dictionary<string,object> local,string publicServer){
  try{
   ValidatePlan(plan,local);var network=Common.Obj(local,"network");string role=Common.Text(plan,"role");
   string next=Common.Hash(Encoding.UTF8.GetBytes(Common.Json(plan)+Common.Text(network,"adapterId")+Common.Text(network,"gateway")));
   if(signature!=next){
    Stop();if(owned!=null)throw new InvalidOperationException("上次网络清理未完成，请先恢复");
    var cfg=loadConfig();bool entry=role=="entry";
    Cli(cfg,entry,entry?"CascadeList":"AccountList");
    string id=Guid.NewGuid().ToString("N").Substring(0,12).ToUpperInvariant();
    owned=Common.Map("account","Link-"+id,"nic","LNK"+id,"role",role,"adapterId",Common.Text(network,"adapterId"),"adapterName",Common.Text(network,"adapterName"),"publicServer",new Uri(publicServer).Host,"gateway",Common.Text(network,"gateway"),"overlayEndpoint",new Uri("https://"+Common.Text(plan,"endpoint")).Host,"remoteGateway",Common.Text(plan,"gateway"),"networks",Strings(plan,"networks").ToArray(),"nicCreated",false,"accountCreated",false,"bridgeCreated",false);
    Save();Guard("pin");
    string name=Common.Text(owned,"account"),nic=Common.Text(owned,"nic"),cert=Path.Combine(Common.Home,"layer2-server.pem");File.WriteAllText(cert,Common.Text(plan,"certificate"));
    if(entry){
     var physical=NetworkDiscovery.Read().Single(a=>a.ID==Common.Text(owned,"adapterId")&&a.Physical&&a.Up&&a.Kind=="ethernet");
     if(NetworkDiscovery.Read().Count(a=>a.Description==physical.Description)!=1||Cli(cfg,true,"BridgeList").Contains(physical.Description))throw new InvalidOperationException("网卡桥接归属不明确，拒绝修改现有桥接");
     // A bridge is anchored to the hardware description, not a renameable TUN display name.
     owned["bridgeDevice"]=physical.Description;Save();
     owned["accountCreated"]=true;Save();Cli(cfg,true,"CascadeCreate "+name+" /SERVER:"+Common.Quote(Common.Text(plan,"endpoint"))+" /HUB:"+Atom(Common.Text(plan,"hub"))+" /USERNAME:"+Atom(Common.Text(plan,"username")));
     Cli(cfg,true,"CascadePasswordSet "+name+" /PASSWORD:"+Atom(Common.Text(plan,"password"))+" /TYPE:standard");
     Cli(cfg,true,"CascadeServerCertSet "+name+" /LOADCERT:"+Common.Quote(cert));Cli(cfg,true,"CascadeServerCertEnable "+name);
     owned["bridgeCreated"]=true;Save();Cli(cfg,true,"BridgeCreate BRIDGE /DEVICE:"+Common.Quote(physical.Description)+" /TAP:no");
     Cli(cfg,true,"CascadeOnline "+name);
    }else{
     owned["nicCreated"]=true;Save();Cli(cfg,false,"NicCreate "+nic);Guard("prepare");
     owned["accountCreated"]=true;Save();Cli(cfg,false,"AccountCreate "+name+" /SERVER:"+Common.Quote(Common.Text(plan,"endpoint"))+" /HUB:"+Atom(Common.Text(plan,"hub"))+" /USERNAME:"+Atom(Common.Text(plan,"username"))+" /NICNAME:"+nic);
     Cli(cfg,false,"AccountPasswordSet "+name+" /PASSWORD:"+Atom(Common.Text(plan,"password"))+" /TYPE:standard");
     Cli(cfg,false,"AccountServerCertSet "+name+" /LOADCERT:"+Common.Quote(cert));Cli(cfg,false,"AccountServerCertEnable "+name);
     Cli(cfg,false,"AccountDetailSet "+name+" /MAXTCP:2 /INTERVAL:1 /TTL:0 /HALF:no /BRIDGE:no /MONITOR:no /NOTRACK:yes /NOQOS:yes /DISABLEUDP:yes");
     Cli(cfg,false,"AccountConnect "+name);
    }
    signature=next;
   }
   Guard("check");
   if(role=="entry")Status=Common.Map("state","entry-ready","message","有线桥接已配置，等待远端验证","ip",Common.Text(local,"lanIp"));
   else{
    string nic=Common.Text(owned,"nic");var adapter=NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(a=>a.Description.EndsWith(" - "+nic,StringComparison.OrdinalIgnoreCase));
    var address=adapter==null?null:adapter.GetIPProperties().UnicastAddresses.FirstOrDefault(a=>a.Address.AddressFamily==System.Net.Sockets.AddressFamily.InterNetwork&&Agent.Private(a.Address)&&Strings(plan,"networks").Any(n=>NetworkDiscovery.Contains(n,a.Address+"/32")));
    Status=Common.Map("state",address==null?"waiting-address":"attached","message",address==null?"等待入口网络分配地址":"已获得局域网地址；双向服务可用性需单独验证","ip",address==null?"":address.Address.ToString());
   }
  }catch(Exception e){Stop();Status=Common.Map("state",owned==null?"blocked":"cleanup-failed","message",e is InvalidOperationException?e.Message:"二层接入未完成，请检查组件与网络配置","ip","");}
 }
 void Guard(string action){var payload=new Dictionary<string,object>(owned);payload["action"]=action;string output=shell(payload);var result=Common.Parse(output.Trim());if(result.ContainsKey("routeOwned")){owned["routeOwned"]=Common.Bool(result,"routeOwned");Save();}}
 internal void Stop(){
  signature="";if(owned==null){Status=Common.Map("state","off","message","局域网接入已断开","ip","");return;}
  bool okay=true;var cfg=new Dictionary<string,object>();if(new[]{"accountCreated","bridgeCreated","nicCreated"}.Any(flag=>Common.Bool(owned,flag)))try{cfg=loadConfig();}catch{okay=false;}
  bool entry=Common.Text(owned,"role")=="entry";string account=Atom(Common.Text(owned,"account"));
  foreach(string flag in new[]{"accountCreated","bridgeCreated","nicCreated"}){
   if(!Common.Bool(owned,flag))continue;
   try{
    if(flag=="accountCreated"){Cli(cfg,entry,(entry?"CascadeOffline ":"AccountDisconnect ")+account,true);Cli(cfg,entry,(entry?"CascadeDelete ":"AccountDelete ")+account,true);}
    if(flag=="bridgeCreated")Cli(cfg,true,"BridgeDelete BRIDGE /DEVICE:"+Common.Quote(Common.Text(owned,"bridgeDevice")),true);
    if(flag=="nicCreated"){Cli(cfg,false,"NicDisable "+Atom(Common.Text(owned,"nic")),true);Cli(cfg,false,"NicDelete "+Atom(Common.Text(owned,"nic")),true);}
    owned[flag]=false;Save();
   }catch{okay=false;}
  }
  try{Guard("cleanup");}catch{okay=false;}
  if(okay){File.Delete(journal);owned=null;Status=Common.Map("state","off","message","局域网接入已断开，Link 网络规则已撤销","ip","");}
  else Status=Common.Map("state","cleanup-failed","message","网络清理未完成，将保留恢复记录并重试","ip","");
 }
 internal void Recover(){Stop();}
}
}
