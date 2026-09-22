using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Link {
internal sealed class Agent : ServiceBase {
 readonly object gate=new object();Dictionary<string,object> config,snapshot=Common.Map();
 readonly Dictionary<string,Forward> forwards=new Dictionary<string,Forward>();
 readonly HashSet<string> failedForwards=new HashSet<string>();
 readonly HashSet<string> localRules=new HashSet<string>();
 Process network;ProcessJob job;bool wanted,stopping;string message="未连接";Timer timer;int ticking;DateTime lastGood=DateTime.MinValue;NamedPipeServerStream activePipe;
 internal Agent(){ServiceName="LinkAgent";CanStop=true;AutoLog=false;}
 protected override void OnStart(string[] args){RequestAdditionalTime(120000);Common.ProtectFolder();CleanOwnedRules();job=new ProcessJob();config=Common.Load();wanted=Common.Bool(config,"autoConnect")&&!Common.Bool(config,"paused")&&Common.Text(config,"token")!="";Task.Run((Action)Serve);timer=new Timer(Tick,null,100,5000);}
 protected override void OnStop(){stopping=true;if(timer!=null)timer.Dispose();if(activePipe!=null)activePipe.Dispose();lock(gate)StopNetwork();if(job!=null)job.Dispose();}
 void Serve(){while(!stopping){try{
  var acl=new PipeSecurity();acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid,null),PipeAccessRights.FullControl,AccessControlType.Deny));
  foreach(var sid in new[]{WellKnownSidType.LocalSystemSid,WellKnownSidType.BuiltinAdministratorsSid})acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(sid,null),PipeAccessRights.FullControl,AccessControlType.Allow));
  using(var pipe=new NamedPipeServerStream("Link.Agent",PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,8192,8192,acl)){
   activePipe=pipe;pipe.WaitForConnection();var read=new StreamReader(pipe,Encoding.UTF8);var write=new StreamWriter(pipe,new UTF8Encoding(false)){AutoFlush=true};
   var readTask=Task.Run(()=>ReadBounded(read));if(!readTask.Wait(10000))continue;
   Dictionary<string,object> result;try{lock(gate)result=Command(Common.Parse(readTask.Result));}catch(WebException e){result=Common.Map("error",NetworkError(e));}catch(Exception e){result=Common.Map("error",e is InvalidOperationException?e.Message:"操作未完成，请检查后台服务");}
   write.WriteLine(Common.Json(result));
  }
 }catch{if(!stopping)Thread.Sleep(200);}}}
 static string ReadBounded(TextReader reader){var b=new StringBuilder();int c;while((c=reader.Read())>=0&&c!='\n'){b.Append((char)c);if(b.Length>16384)throw new IOException();}return b.ToString();}
 static string NetworkError(WebException e){var r=e.Response as HttpWebResponse;if(r!=null&&(int)r.StatusCode==403)return "设备未获授权，请联系管理员";return "连接未完成，请检查服务端地址、加入码与网络";}
 Dictionary<string,object> Command(Dictionary<string,object> request){string action=Common.Text(request,"action");
  if(action=="status")return Common.Map("message",message,"wanted",wanted,"registered",Common.Text(config,"token")!="","autoStart",Common.Bool(config,"autoStart"),"autoConnect",Common.Bool(config,"autoConnect"),"state",snapshot,"version",Common.Version);
  if(action=="enroll"){
   if(Common.Text(config,"token")!="")throw new InvalidOperationException("本机已加入网络；更换实例前请卸载并清除本机身份");
   string endpoint=Common.Text(request,"server").Trim().TrimEnd('/'),code=Common.Text(request,"code").Trim();Common.ValidateEndpoint(endpoint);
   string[] parts=code.Split('.');if(parts.Length!=3||parts[0]!="LINK1"||parts[1].Length!=64)throw new InvalidOperationException("加入码格式不正确");
   var pending=Common.Map("server",endpoint,"pin",parts[1],"token","");
   byte[] cert=Common.Request(endpoint+"/bootstrap/ca",parts[1],"",null);if(Common.Hash(Common.ReadCertificate(cert).RawData)!=parts[1])throw new InvalidOperationException("证书指纹不匹配");
   var enrolled=Common.Api(pending,"/agent/enroll",Common.Map("code",code,"name",Environment.MachineName,"os","Windows"));
   pending["token"]=Common.Text(enrolled,"token");pending["deviceId"]=Common.Text(enrolled,"deviceId");pending["role"]=Common.Text(enrolled,"role");pending["autoStart"]=true;pending["autoConnect"]=true;pending["paused"]=false;
   // Persist identity before starting networking so a startup failure cannot consume enrollment twice.
   config=pending;Common.Save(config);File.WriteAllBytes(Path.Combine(Common.Home,"ca.pem"),cert);Common.Trust(cert,parts[1]);
   wanted=true;StartNetwork(Common.Text(enrolled,"setupKey"));message="正在连接";return Common.Map("ok",true);
  }
  if(action=="connect"){
   if(Common.Text(config,"token")=="")throw new InvalidOperationException("请先加入网络");var reply=Common.Api(config,"/agent/reconnect",Common.Map());
   StopNetwork();config["paused"]=false;Common.Save(config);wanted=true;StartNetwork(Common.Text(reply,"setupKey"));message="正在连接";return Common.Map("ok",true);
  }
  if(action=="disconnect"){Pause("已断开");return Common.Map("ok",true);}
  if(action=="settings"){
   bool autoStart=Common.Bool(request,"autoStart"),autoConnect=Common.Bool(request,"autoConnect");Common.Run("sc.exe","config LinkAgent start= "+(autoStart?"auto":"demand"));config["autoStart"]=autoStart;config["autoConnect"]=autoConnect;Common.Save(config);return Common.Map("ok",true);
  }
  if(action=="browser"){var reply=Common.Api(config,"/agent/browser-ticket",Common.Map());return reply;}
  throw new InvalidOperationException("不支持的操作");
 }
 void Pause(string reason){wanted=false;config["paused"]=true;Common.Save(config);StopNetwork();snapshot=Common.Map();message=reason;}
 void Tick(object state){if(Interlocked.Exchange(ref ticking,1)==1)return;try{lock(gate){if(stopping||!wanted)return;
  try{
   var local=Discover();var states=Common.Map();foreach(var id in failedForwards)states[id]="error";
   var checks=forwards.Select(item=>new {ID=item.Key,Check=Task.Run(()=>item.Value.Healthy())}).ToArray();Task.WaitAll(checks.Select(c=>(Task)c.Check).ToArray(),2000);foreach(var check in checks)states[check.ID]=check.Check.Status==TaskStatus.RanToCompletion&&check.Check.Result?"ready":"error";
   local["mappingStates"]=states;local["applications"]=Applications();var reply=Common.Api(config,"/agent/heartbeat",local);
   if(Common.Text(reply,"action")=="disconnect"){Pause("已被断开，请手动重新连接");return;}
   lastGood=DateTime.UtcNow;snapshot=reply;
   if(network==null||network.HasExited){StartNetwork("");message="正在连接";}
   ApplyForwards(Common.Items(reply,"forwards"));ApplyLocalRules(reply);var device=Common.Obj(reply,"device");message=Common.Bool(device,"connected")?"已连接":"正在建立网络连接";
  }catch(WebException e){var response=e.Response as HttpWebResponse;if(response!=null&&(int)response.StatusCode==403){Pause("设备已停用");return;}message="连接中断，正在重试";if(DateTime.UtcNow-lastGood>TimeSpan.FromSeconds(35)){StopNetwork();snapshot=Common.Map();}}
   catch{message="网络配置未完成，请重试连接";if(DateTime.UtcNow-lastGood>TimeSpan.FromSeconds(35))StopNetwork();}
 }}finally{Interlocked.Exchange(ref ticking,0);}}
 void StartNetwork(string setupKey){
  if(network!=null&&!network.HasExited)return;string executable=Path.Combine(Common.Bin,"netbird.exe");if(!File.Exists(executable))throw new InvalidOperationException("安装包缺少网络组件");
  string keyFile=Path.Combine(Common.Home,"setup-key");if(setupKey!="")File.WriteAllText(keyFile,setupKey);
  string args="up --foreground-mode --no-browser --disable-dns --disable-ipv6 --interface-name Link0 --wireguard-port 51821 --config "+Common.Quote(Path.Combine(Common.Home,"network.json"))+" --management-url "+Common.Quote(Common.Text(config,"server"))+" --log-file "+Common.Quote(Path.Combine(Common.Home,"network.log"))+" --log-level warn";
  if(setupKey!="")args+=" --setup-key-file "+Common.Quote(keyFile);
  var info=new ProcessStartInfo(executable,args){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};info.EnvironmentVariables["NB_ENABLE_LOCAL_FORWARDING"]="true";
  network=new Process{StartInfo=info};network.OutputDataReceived+=(s,e)=>{};network.ErrorDataReceived+=(s,e)=>{};network.Start();job.Add(network);network.BeginOutputReadLine();network.BeginErrorReadLine();
 }
 void StopNetwork(){foreach(var f in forwards.Values)f.Dispose();forwards.Clear();failedForwards.Clear();foreach(var rule in localRules)RemoveRule(rule);localRules.Clear();
  if(network!=null){try{if(!network.HasExited){network.Kill();network.WaitForExit(10000);}}catch{}network.Dispose();network=null;}
  string keyFile=Path.Combine(Common.Home,"setup-key");if(File.Exists(keyFile))File.Delete(keyFile);
 }
 internal static Dictionary<string,object> Discover(){
  var networks=new List<string>();string lan="";
  foreach(var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.OperationalStatus==OperationalStatus.Up&&n.NetworkInterfaceType!=NetworkInterfaceType.Loopback&&n.Name!="Link0"&&!n.Description.ToLowerInvariant().Contains("wireguard"))){
   var properties=nic.GetIPProperties();if(!properties.GatewayAddresses.Any(g=>g.Address.AddressFamily==AddressFamily.InterNetwork&&!g.Address.Equals(IPAddress.Any)))continue;
   foreach(var address in properties.UnicastAddresses.Where(a=>a.Address.AddressFamily==AddressFamily.InterNetwork&&Private(a.Address))){
    if(lan=="")lan=address.Address.ToString();var bytes=address.Address.GetAddressBytes();var mask=address.IPv4Mask.GetAddressBytes();int bits=mask.Sum(x=>Convert.ToString(x,2).Count(c=>c=='1'));
    if(bits<16||bits>32)continue;for(int i=0;i<4;i++)bytes[i]&=mask[i];networks.Add(new IPAddress(bytes)+"/"+bits);
   }
  }
  networks.AddRange(PrivateRoutes());return Common.Map("lanIp",lan,"networks",networks.Distinct().OrderBy(n=>Int32.Parse(n.Split('/')[1])).Where(n=>!networks.Any(other=>other!=n&&ContainsNetwork(other,n))).Take(16).ToArray());
 }
 static bool ContainsNetwork(string outer,string inner){var a=outer.Split('/');var b=inner.Split('/');int bits=Int32.Parse(a[1]);if(bits>Int32.Parse(b[1]))return false;var x=IPAddress.Parse(a[0]).GetAddressBytes();var y=IPAddress.Parse(b[0]).GetAddressBytes();for(int i=0;i<bits;i++)if((x[i/8]&(1<<(7-i%8)))!=(y[i/8]&(1<<(7-i%8))))return false;return true;}
 [DllImport("iphlpapi.dll")]static extern int GetIpForwardTable(IntPtr table,ref int size,bool order);
 static IEnumerable<string> PrivateRoutes(){
  var result=new List<string>();int size=0;GetIpForwardTable(IntPtr.Zero,ref size,false);if(size<4||size>1048576)return result;var buffer=Marshal.AllocHGlobal(size);
  try{if(GetIpForwardTable(buffer,ref size,false)!=0)return result;var excluded=new HashSet<int>(NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.Name=="Link0"||n.Description.ToLowerInvariant().Contains("wireguard")).Select(n=>n.GetIPProperties().GetIPv4Properties()).Where(p=>p!=null).Select(p=>p.Index));
   int count=Marshal.ReadInt32(buffer);for(int i=0;i<count&&4+(i+1)*56<=size;i++){var row=IntPtr.Add(buffer,4+i*56);if(excluded.Contains(Marshal.ReadInt32(row,16)))continue;var dest=new IPAddress(BitConverter.GetBytes(Marshal.ReadInt32(row)));var mask=BitConverter.GetBytes(Marshal.ReadInt32(row,4));int bits=mask.Sum(x=>Convert.ToString(x,2).Count(c=>c=='1'));if(Private(dest)&&bits>=16&&bits<=30)result.Add(dest+"/"+bits);}
  }finally{Marshal.FreeHGlobal(buffer);}return result;
 }
 internal static bool Private(IPAddress ip){var b=ip.GetAddressBytes();return b.Length==4&&(b[0]==10||(b[0]==172&&b[1]>=16&&b[1]<=31)||(b[0]==192&&b[1]==168));}
 static object[] Applications(){return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Where(e=>e.Port>0).Select(e=>e.Port).Distinct().OrderBy(p=>p).Take(100).Select(p=>(object)Common.Map("name","TCP 服务","port",p)).ToArray();}
 static void RemoveRule(string name){try{Common.Run("netsh.exe","advfirewall firewall delete rule name="+Common.Quote(name));}catch{}}
 static void CleanOwnedRules(){
  object policy=Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2"));object rules=policy.GetType().InvokeMember("Rules",System.Reflection.BindingFlags.GetProperty,null,policy,null);var names=new List<string>();
  foreach(object rule in (System.Collections.IEnumerable)rules){string name=(string)rule.GetType().InvokeMember("Name",System.Reflection.BindingFlags.GetProperty,null,rule,null);if(name.StartsWith("Link-Map-",StringComparison.Ordinal)||name.StartsWith("Link-Service-",StringComparison.Ordinal))names.Add(name);}
  foreach(var name in names)RemoveRule(name);
 }
 void ApplyLocalRules(Dictionary<string,object> reply){var self=Common.Obj(reply,"device");string ip=Common.Text(self,"ip");IPAddress parsed;if(!IPAddress.TryParse(ip,out parsed))return;var desired=new HashSet<string>();
  foreach(var mapping in Common.Items(reply,"mappings").Where(m=>Common.Text(m,"deviceId")==Common.Text(self,"id"))){int port=Convert.ToInt32(mapping["port"]);string name="Link-Service-"+Common.Text(mapping,"id")+"-"+ip;if(port<1||port>65535)continue;desired.Add(name);if(localRules.Contains(name))continue;
   Common.Run("netsh.exe","advfirewall firewall add rule name="+Common.Quote(name)+" dir=in action=allow protocol=TCP localip="+ip+" localport="+port+" remoteip=100.64.0.0/10 profile=any");localRules.Add(name);
  }
  foreach(var name in localRules.Where(r=>!desired.Contains(r)).ToList()){RemoveRule(name);localRules.Remove(name);}
 }
 void ApplyForwards(IEnumerable<Dictionary<string,object>> desired){
  var list=desired.ToList();failedForwards.RemoveWhere(id=>!list.Any(d=>Common.Text(d,"id")==id));foreach(string id in forwards.Keys.Where(id=>!list.Any(d=>Common.Text(d,"id")==id)).ToList()){forwards[id].Dispose();forwards.Remove(id);}
  foreach(var d in list){string id=Common.Text(d,"id"),signature=Common.Json(d);Forward old;
   if(forwards.TryGetValue(id,out old)&&old.Signature==signature)continue;if(old!=null){old.Dispose();forwards.Remove(id);}
   try{forwards[id]=new Forward(d);failedForwards.Remove(id);}catch{failedForwards.Add(id);message="服务映射无法启动";}
  }
 }
}
internal sealed class Forward : IDisposable {
 internal readonly string Signature;readonly TcpListener listener;readonly string target;readonly int port;readonly List<TcpClient> connections=new List<TcpClient>();bool closed;readonly string firewall;
 internal Forward(Dictionary<string,object> d){Signature=Common.Json(d);target=Common.Text(d,"targetIp");port=Convert.ToInt32(d["targetPort"]);IPAddress targetIP,listenIP;
  if(!IPAddress.TryParse(target,out targetIP)||targetIP.AddressFamily!=AddressFamily.InterNetwork||targetIP.GetAddressBytes()[0]!=100||targetIP.GetAddressBytes()[1]<64||targetIP.GetAddressBytes()[1]>127||!IPAddress.TryParse(Common.Text(d,"listenIp"),out listenIP)||!Agent.Private(listenIP))throw new InvalidOperationException("映射地址无效");
  int localPort=Convert.ToInt32(d["listenPort"]);if(localPort<22000||localPort>22999||port<1||port>65535)throw new InvalidOperationException("映射端口无效");
  firewall="Link-Map-"+Common.Text(d,"id");listener=new TcpListener(listenIP,localPort);listener.Start();
  try{Common.Run("netsh.exe","advfirewall firewall add rule name="+Common.Quote(firewall)+" dir=in action=allow protocol=TCP localip="+listenIP+" localport="+localPort+" remoteip=10.0.0.0/8,172.16.0.0/12,192.168.0.0/16 profile=any");Task.Run((Action)Accept);}catch{listener.Stop();throw;}
 }
 async void Accept(){while(!closed){TcpClient incoming=null;try{incoming=await listener.AcceptTcpClientAsync();lock(connections){if(connections.Count>=256){incoming.Close();continue;}connections.Add(incoming);}Dispatch(incoming);}catch{if(incoming!=null)incoming.Close();if(!closed)Thread.Sleep(200);}}}
 void Dispatch(TcpClient accepted){Task.Run(()=>Pump(accepted));}
 async Task Pump(TcpClient source){var dest=new TcpClient();lock(connections)connections.Add(dest);try{
  var connecting=dest.ConnectAsync(target,port);if(await Task.WhenAny(connecting,Task.Delay(5000))!=connecting)return;await connecting;
  await Relay(source,dest);
 }catch{}finally{source.Close();dest.Close();lock(connections){connections.Remove(source);connections.Remove(dest);}}}
 internal static async Task Relay(TcpClient left,TcpClient right){await Task.WhenAll(CopyHalf(left,right),CopyHalf(right,left));}
 static async Task CopyHalf(TcpClient source,TcpClient target){try{await source.GetStream().CopyToAsync(target.GetStream());target.Client.Shutdown(SocketShutdown.Send);}catch{source.Close();target.Close();}}
 internal bool Healthy(){if(closed)return false;using(var client=new TcpClient()){try{var task=client.ConnectAsync(target,port);return task.Wait(750)&&client.Connected;}catch{return false;}}}
 public void Dispose(){if(closed)return;closed=true;listener.Stop();lock(connections){foreach(var c in connections)c.Close();connections.Clear();}try{Common.Run("netsh.exe","advfirewall firewall delete rule name="+Common.Quote(firewall));}catch{}}
}
}
