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
internal sealed class TransportPending : InvalidOperationException { internal TransportPending(string message):base(message){} }
// An optional, explicitly prepared SoftEther installation supplies the signed adapter driver.
// Only journalled Link accounts, bridges and virtual adapters are ever modified.
internal sealed class Layer2 {
 readonly string journal;
 readonly Func<Dictionary<string,object>> loadConfig;
 readonly Func<Dictionary<string,object>,bool,string,bool,string> command;
 readonly Func<Dictionary<string,object>,string> shell;
 Dictionary<string,object> owned;string signature="";long transportWaiting=-1;
 internal bool WaitForTransport(string message,long now){
  if(transportWaiting<0)transportWaiting=now;
  if(now-transportWaiting>30L*Stopwatch.Frequency)return false;
  Status=Common.Map("state","waiting-network","message",message+"；等待网络恢复（最多 30 秒）","ip","");return true;
 }
 internal void RecordFailure(string message){try{File.WriteAllText(Path.Combine(Path.GetDirectoryName(journal),"layer2-last-error.json"),Common.Json(Common.Map("time",DateTime.UtcNow.ToString("o"),"message",message)));}catch(IOException){}catch(UnauthorizedAccessException){}}
 internal Dictionary<string,object> Status=Common.Map("state","off","message","未启用局域网接入","ip","");
 internal Layer2():this(Common.Home,LocalConfig,ExecuteCli,Shell){}
 internal Layer2(string directory,Func<Dictionary<string,object>> config,Func<Dictionary<string,object>,bool,string,bool,string> cli,Func<Dictionary<string,object>,string> guard){journal=Path.Combine(directory,"layer2-journal.json");loadConfig=config;command=cli;shell=guard;if(File.Exists(journal))owned=Common.Parse(File.ReadAllText(journal));}
 string Cli(Dictionary<string,object> config,bool entry,string text,bool cleanup=false){try{return command(config,entry,text,cleanup);}catch(Exception error){throw new InvalidOperationException("二层操作 "+text.Split(' ')[0]+"："+error.Message,error);}}
 internal static string AvailableNic(string listing,IEnumerable<string> descriptions){
  var used=new HashSet<string>(Regex.Matches(listing+"\n"+string.Join("\n",descriptions),@"\bVPN(?:[0-9]+)?\b",RegexOptions.IgnoreCase).Cast<Match>().Select(m=>m.Value),StringComparer.OrdinalIgnoreCase);
  for(int i=127;i>=1;i--){string name=i==1?"VPN":"VPN"+i;if(!used.Contains(name))return name;}
  throw new InvalidOperationException("没有可用的虚拟网卡名称，请保留现有网卡并检查组件");
 }
 internal void CreateNic(Dictionary<string,object> cfg){
  Cli(cfg,false,"NicPrepare "+Atom(Common.Text(owned,"nic")));
  owned["nicPending"]=true;Save();
  try{Cli(cfg,false,"NicCreate "+Atom(Common.Text(owned,"nic")));}
  catch(InvalidOperationException e){var failure=e.InnerException as CommandFailure;if(failure!=null&&(failure.ExitCode==30||failure.ExitCode==32)){owned["nicPending"]=false;Save();}throw;}
  // Only a successful create can claim an adapter. An interrupted create is retained for review.
  Guard("identify");owned["nicCreated"]=true;owned["nicPending"]=false;Save();
 }
 internal static string Atom(string s){if(!Regex.IsMatch(s??"","^[A-Za-z0-9_-]{1,80}$"))throw new InvalidOperationException("二层组件参数无效");return s;}
 internal static string BridgeDevice(string description,string adapterID,string listing){
  Guid guid;if(!Guid.TryParse(adapterID,out guid))throw new InvalidOperationException("物理网卡标识无效");
  // SoftEther Stable BridgeWin32.c: SHA-1 of uppercase brace-form GUID, first 32 bits big endian.
  byte[] hash;using(var sha=SHA1.Create())hash=sha.ComputeHash(Encoding.ASCII.GetBytes(guid.ToString("B").ToUpperInvariant()));
  uint id=((uint)hash[0]<<24)|((uint)hash[1]<<16)|((uint)hash[2]<<8)|hash[3];if(id==0)id=1;
  string expected=description+" (ID="+id.ToString("D10",System.Globalization.CultureInfo.InvariantCulture)+")";
  if(!listing.Split('\n').SelectMany(line=>line.Split('|')).Any(cell=>cell.Trim()==expected))throw new InvalidOperationException("桥接组件未报告所选物理网卡，请修复组件后重试");
  return expected;
 }
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
  if(!entry&&command.StartsWith("NicPrepare ")){SoftEtherDriver.Prepare(command.Substring(11));return "";}
  if(!entry&&command.StartsWith("NicCreate "))return SoftEtherDriver.CreatePrepared(command.Substring(10));
  string password=Common.Text(config,entry?"bridgePassword":"clientPassword");if(password.Length<20)throw new InvalidOperationException("二层本机管理凭据尚未配置");
  string input=Path.Combine(Common.Home,"layer2-command-"+Guid.NewGuid().ToString("N")+".txt");
  // Device credentials go in this SYSTEM/Administrators-only temporary input, never command arguments.
  // vpncmd treats a UTF-8 BOM as part of the first command name.
  File.WriteAllText(input,command+"\r\nexit\r\n",new UTF8Encoding(false));
  try{return Common.Run(exe,"127.0.0.1"+(entry?":5555 /SERVER":" /CLIENT")+" /PROGRAMMING /PASSWORD:"+Common.Quote(password)+(entry?" /ADMINHUB:BRIDGE":"")+" /IN:"+Common.Quote(input),12000,cleanup?new[]{29,36,37,61,76}:new int[0]);}finally{File.Delete(input);}
 }
 internal static void CheckComponents(){var config=LocalConfig();ExecuteCli(config,false,"AccountList",false);ExecuteCli(config,true,"CascadeList",false);}
 static string Shell(Dictionary<string,object> payload){
  string request=Path.Combine(Common.Home,"layer2-request-"+Guid.NewGuid().ToString("N")+".json");
  string script=Path.Combine(Common.Home,"layer2-network.ps1");
  using(var input=System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("Link.Layer2Network"))using(var target=File.Create(script))input.CopyTo(target);
  File.WriteAllText(request,Common.Json(payload));
  try{return Common.Run(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell\\v1.0\\powershell.exe"),"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "+Common.Quote(script)+" -Request "+Common.Quote(request),12000,1);}finally{File.Delete(request);}
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
 internal void ConnectMember(Dictionary<string,object> cfg,Dictionary<string,object> plan,string name,string nic,string cert){
  Cli(cfg,false,"AccountCreate "+name+" /SERVER:"+Common.Quote(Common.Text(plan,"endpoint"))+" /HUB:"+Atom(Common.Text(plan,"hub"))+" /USERNAME:"+Atom(Common.Text(plan,"username"))+" /NICNAME:"+nic);
  Cli(cfg,false,"AccountPasswordSet "+name+" /PASSWORD:"+Atom(Common.Text(plan,"password"))+" /TYPE:standard");
  Cli(cfg,false,"AccountServerCertSet "+name+" /LOADCERT:"+Common.Quote(cert));Cli(cfg,false,"AccountServerCertEnable "+name);
  // Stable 4.44 has no /DISABLEUDP option. The dedicated hub disables UDP acceleration.
  Cli(cfg,false,"AccountDetailSet "+name+" /MAXTCP:2 /INTERVAL:1 /TTL:0 /HALF:no /BRIDGE:no /MONITOR:no /NOTRACK:yes /NOQOS:yes");
  Cli(cfg,false,"AccountConnect "+name);
 }
 internal void Apply(Dictionary<string,object> plan,Dictionary<string,object> local,string publicServer){
  try{
   ValidatePlan(plan,local);var network=Common.Obj(local,"network");string role=Common.Text(plan,"role");
   string next=Common.Hash(Encoding.UTF8.GetBytes(Common.Json(plan)+Common.Text(network,"adapterId")+Common.Text(network,"gateway")));
   if(signature!=next){
    var cfg=loadConfig();bool entry=role=="entry";
    if(owned==null||Common.Text(owned,"planSignature")!=next){
    Stop();if(owned!=null)throw new InvalidOperationException("上次网络清理未完成，请先恢复");
    Cli(cfg,entry,entry?"CascadeList":"AccountList");
    string id=Guid.NewGuid().ToString("N").Substring(0,12).ToUpperInvariant();
    owned=Common.Map("account","Link-"+id,"nic",entry?"LNK"+id:AvailableNic(Cli(cfg,false,"NicList"),NetworkInterface.GetAllNetworkInterfaces().Select(a=>a.Description)),"role",role,"adapterId",Common.Text(network,"adapterId"),"adapterName",Common.Text(network,"adapterName"),"publicServer",new Uri(publicServer).Host,"gateway",Common.Text(network,"gateway"),"overlayEndpoint",new Uri("https://"+Common.Text(plan,"endpoint")).Host,"remoteGateway",Common.Text(plan,"gateway"),"networks",Strings(plan,"networks").ToArray(),"nicCreated",false,"accountCreated",false,"bridgeCreated",false);
    owned["planSignature"]=next;Save();
    }
    Guard("pin");
    string name=Common.Text(owned,"account"),nic=Common.Text(owned,"nic"),cert=Path.Combine(Common.Home,"layer2-server.pem");File.WriteAllText(cert,Common.Text(plan,"certificate"));
    if(entry){
     var physical=NetworkDiscovery.Read().Single(a=>a.ID==Common.Text(owned,"adapterId")&&a.Physical&&a.Up&&a.Kind=="ethernet");
     if(NetworkDiscovery.Read().Count(a=>a.Description==physical.Description)!=1||Cli(cfg,true,"BridgeList").Contains(physical.Description))throw new InvalidOperationException("网卡桥接归属不明确，拒绝修改现有桥接");
     // A bridge is anchored to the hardware description, not a renameable TUN display name.
     owned["bridgeDevice"]=BridgeDevice(physical.Description,physical.ID,Cli(cfg,true,"BridgeDeviceList"));Save();
     owned["accountCreated"]=true;Save();Cli(cfg,true,"CascadeCreate "+name+" /SERVER:"+Common.Quote(Common.Text(plan,"endpoint"))+" /HUB:"+Atom(Common.Text(plan,"hub"))+" /USERNAME:"+Atom(Common.Text(plan,"username")));
     Cli(cfg,true,"CascadePasswordSet "+name+" /PASSWORD:"+Atom(Common.Text(plan,"password"))+" /TYPE:standard");
     Cli(cfg,true,"CascadeServerCertSet "+name+" /LOADCERT:"+Common.Quote(cert));Cli(cfg,true,"CascadeServerCertEnable "+name);
     owned["bridgeCreated"]=true;Save();Cli(cfg,true,"BridgeCreate BRIDGE /DEVICE:"+Common.Quote(Common.Text(owned,"bridgeDevice"))+" /TAP:no");
     Cli(cfg,true,"CascadeOnline "+name);
    }else{
     if(!Common.Bool(owned,"nicCreated"))CreateNic(cfg);Guard("prepare");
     owned["accountCreated"]=true;Save();ConnectMember(cfg,plan,name,nic,cert);
    }
    signature=next;
   }
   Guard("check");transportWaiting=-1;
   if(role=="entry")Status=Common.Map("state","entry-ready","message","有线桥接已配置，等待远端验证","ip",Common.Text(local,"lanIp"));
   else{
    string nic=Common.Text(owned,"nic");var adapter=NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(a=>a.Description.EndsWith(" - "+nic,StringComparison.OrdinalIgnoreCase));
    var address=adapter==null?null:adapter.GetIPProperties().UnicastAddresses.FirstOrDefault(a=>a.Address.AddressFamily==System.Net.Sockets.AddressFamily.InterNetwork&&a.DuplicateAddressDetectionState==DuplicateAddressDetectionState.Preferred&&Agent.Private(a.Address)&&Strings(plan,"networks").Any(n=>NetworkDiscovery.Contains(n,a.Address+"/32")));
    Status=Common.Map("state",address==null?"waiting-address":"attached","message",address==null?"等待入口网络分配地址":"已获得局域网地址；双向服务可用性需单独验证","ip",address==null?"":address.Address.ToString());
   }
  }catch(Exception e){if(e is TransportPending&&WaitForTransport(e.Message,Stopwatch.GetTimestamp()))return;RecordFailure(e.Message);if(owned!=null){owned["lastFailure"]=e is InvalidOperationException?e.Message:"二层组件操作失败";Save();}Stop();if(owned==null)Status=Common.Map("state","blocked","message",e is InvalidOperationException?e.Message:"二层接入未完成，请检查组件与网络配置","ip","");}
 }
 void Guard(string action){
  var payload=new Dictionary<string,object>(owned);payload["action"]=action;var result=Common.Parse(shell(payload).Trim());
  if(result.ContainsKey("error"))throw new InvalidOperationException("网络检查 "+action+"："+Common.Text(result,"error"));
  if(result.ContainsKey("nicId")){owned["nicId"]=Common.Text(result,"nicId");Save();}
  if(result.ContainsKey("nicPresent"))owned["nicPresent"]=Common.Bool(result,"nicPresent");
  if(result.ContainsKey("routeOwned")){owned["routeOwned"]=Common.Bool(result,"routeOwned");Save();}
  if(result.ContainsKey("waiting")){
   string detail=Common.Text(result,"waiting")=="lan-route"?"局域网路由正在恢复":"专用网络路由正在恢复";
   if(result.ContainsKey("expectedInterface"))detail+="（当前网卡 "+(Common.Text(result,"actualInterface")==""?"无可用路由":Common.Text(result,"actualInterface"))+"，期望网卡 "+Common.Text(result,"expectedInterface")+"，路由 "+Common.Text(result,"prefix")+"）";
   RecordFailure(detail);throw new TransportPending(detail);
  }
 }
 internal void Stop(){
  signature="";transportWaiting=-1;if(owned==null){Status=Common.Map("state","off","message","局域网接入已断开","ip","");return;}
  var failures=new List<string>();if(Common.Bool(owned,"nicPending"))failures.Add("虚拟网卡创建结果不明，请保留恢复记录");var cfg=new Dictionary<string,object>();if(new[]{"accountCreated","bridgeCreated","nicCreated"}.Any(flag=>Common.Bool(owned,flag)))try{cfg=loadConfig();}catch{failures.Add("本机组件配置");}
  bool entry=Common.Text(owned,"role")=="entry";string account=Atom(Common.Text(owned,"account"));
  foreach(string flag in new[]{"accountCreated","bridgeCreated","nicCreated"}){
   if(!Common.Bool(owned,flag))continue;
   try{
    if(flag=="accountCreated"){Cli(cfg,entry,(entry?"CascadeOffline ":"AccountDisconnect ")+account,true);Cli(cfg,entry,(entry?"CascadeDelete ":"AccountDelete ")+account,true);}
    if(flag=="bridgeCreated")Cli(cfg,true,"BridgeDelete BRIDGE /DEVICE:"+Common.Quote(Common.Text(owned,"bridgeDevice")),true);
    if(flag=="nicCreated"){if(!Common.Text(owned,"nic").StartsWith("LNK")){Guard("verify-nic");if(!Common.Bool(owned,"nicPresent")){owned[flag]=false;Save();continue;}}Cli(cfg,false,"NicDisable "+Atom(Common.Text(owned,"nic")),true);Cli(cfg,false,"NicDelete "+Atom(Common.Text(owned,"nic")),true);}
    owned[flag]=false;Save();
   }catch{failures.Add(flag=="accountCreated"?"连接":flag=="bridgeCreated"?"网卡桥接":"虚拟网卡");}
  }
  try{Guard("cleanup");}catch{failures.Add("网络路由");}
  if(failures.Count==0){File.Delete(journal);owned=null;Status=Common.Map("state","off","message","局域网接入已断开，Link 网络规则已撤销","ip","");}
  else Status=Common.Map("state","cleanup-failed","message","网络清理未完成（"+string.Join("、",failures)+"），将保留恢复记录并重试。"+Common.Text(owned,"lastFailure"),"ip","");
 }
 internal void Recover(){Stop();}
}
}
