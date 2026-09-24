using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Web.Script.Serialization;

namespace Link {
internal sealed class CommandFailure : InvalidOperationException { internal readonly int ExitCode; internal CommandFailure(int code):base("系统操作失败，退出码 "+code){ExitCode=code;} }
internal static class Common {
 internal const string Version = "0.2.0-alpha.12";
 internal static readonly string Home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Link");
 internal static string Bin { get { return AppDomain.CurrentDomain.BaseDirectory; } }
 internal static Dictionary<string, object> Map(params object[] pairs) { var d=new Dictionary<string,object>();for(int i=0;i<pairs.Length;i+=2)d[(string)pairs[i]]=pairs[i+1];return d; }
 internal static string Json(object o) {return new JavaScriptSerializer().Serialize(o);}
 internal static Dictionary<string,object> Parse(string s){return new JavaScriptSerializer{MaxJsonLength=1048576}.Deserialize<Dictionary<string,object>>(s);}
 internal static string Text(Dictionary<string,object> d,string key,string fallback=""){object v;return d!=null&&d.TryGetValue(key,out v)&&v!=null?Convert.ToString(v):fallback;}
 internal static bool Bool(Dictionary<string,object> d,string key){return Text(d,key).Equals("True",StringComparison.OrdinalIgnoreCase);}
 internal static Dictionary<string,object> Obj(Dictionary<string,object> d,string key){object v;return d!=null&&d.TryGetValue(key,out v)?v as Dictionary<string,object>:null;}
 internal static IEnumerable<Dictionary<string,object>> Items(Dictionary<string,object> d,string key){object v;if(d==null||!d.TryGetValue(key,out v)||v==null)return new Dictionary<string,object>[0];return ((System.Collections.IEnumerable)v).Cast<object>().OfType<Dictionary<string,object>>();}
 internal static string Hash(byte[] b){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(b)).Replace("-","").ToLowerInvariant();}
 internal static string Quote(string s){if(s.Contains("\"")||s.Contains("\r")||s.Contains("\n"))throw new InvalidOperationException("参数包含无效字符");return "\""+s+"\"";}
 internal static void ProtectFolder(){
  Directory.CreateDirectory(Home);var acl=new DirectorySecurity();acl.SetAccessRuleProtection(true,false);
  foreach(var sid in new[]{WellKnownSidType.LocalSystemSid,WellKnownSidType.BuiltinAdministratorsSid})acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid,null),FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
  Directory.SetAccessControl(Home,acl);
 }
 internal static void Save(Dictionary<string,object> d){
  var bytes=ProtectedData.Protect(Encoding.UTF8.GetBytes(Json(d)),null,DataProtectionScope.LocalMachine);
  string path=Path.Combine(Home,"device.bin"),temp=path+".tmp";File.WriteAllBytes(temp,bytes);if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);
 }
 internal static Dictionary<string,object> Load(){string p=Path.Combine(Home,"device.bin");return File.Exists(p)?Parse(Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(p),null,DataProtectionScope.LocalMachine))):Map("autoConnect",true,"autoStart",true);}
 internal static string Run(string exe,string args,int timeout=15000,params int[] acceptedExitCodes){
  var output=new StringBuilder();using(var p=new Process()){p.StartInfo=new ProcessStartInfo(exe,args){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
   p.OutputDataReceived+=(s,e)=>{if(e.Data!=null)lock(output)output.AppendLine(e.Data);};p.ErrorDataReceived+=(s,e)=>{};p.Start();p.BeginOutputReadLine();p.BeginErrorReadLine();
   if(!p.WaitForExit(timeout)){p.Kill();throw new InvalidOperationException("操作超时");}p.WaitForExit();if(p.ExitCode!=0&&!acceptedExitCodes.Contains(p.ExitCode))throw new CommandFailure(p.ExitCode);return output.ToString();}
 }
 internal static Dictionary<string,object> Pipe(Dictionary<string,object> command){using(var pipe=new NamedPipeClientStream(".","Link.Agent",PipeDirection.InOut,PipeOptions.None)){pipe.Connect(2500);var writer=new StreamWriter(pipe,new UTF8Encoding(false)){AutoFlush=true};var reader=new StreamReader(pipe,Encoding.UTF8);writer.WriteLine(Json(command));string line=reader.ReadLine();if(line==null)throw new IOException("后台服务未响应");var result=Parse(line);if(result.ContainsKey("error"))throw new InvalidOperationException(Text(result,"error"));return result;}}
 internal static void ValidateEndpoint(string endpoint){Uri uri;IPAddress ip;if(!Uri.TryCreate(endpoint,UriKind.Absolute,out uri)||uri.Scheme!="https"||!IPAddress.TryParse(uri.Host,out ip)||uri.AbsolutePath!="/"||uri.UserInfo!=""||uri.Query!=""||uri.Fragment!="")throw new InvalidOperationException("请输入 HTTPS 服务端 IP 地址，例如 https://203.0.113.10:24443");}
 internal static HttpWebRequest NewRequest(string url,string pin,string token){
  var req=(HttpWebRequest)WebRequest.Create(url);req.Proxy=null;req.AllowAutoRedirect=false;req.Timeout=15000;req.ReadWriteTimeout=15000;
  req.ServerCertificateValidationCallback=(sender,certificate,chain,errors)=>{
   if(certificate==null||chain==null||(errors&SslPolicyErrors.RemoteCertificateNameMismatch)!=0)return false;
   if(chain.ChainElements.Count==0||Hash(chain.ChainElements[chain.ChainElements.Count-1].Certificate.RawData)!=pin)return false;
   return chain.ChainStatus.All(s=>s.Status==X509ChainStatusFlags.NoError||s.Status==X509ChainStatusFlags.UntrustedRoot);
  };
  if(token!="")req.Headers["Authorization"]="Bearer "+token;
  req.ServicePoint.ConnectionLimit=Math.Max(8,req.ServicePoint.ConnectionLimit);
  return req;
 }
 internal static byte[] Request(string url,string pin,string token,object data){
  var req=NewRequest(url,pin,token);
  if(data!=null){req.Method="POST";req.ContentType="application/json";var bytes=Encoding.UTF8.GetBytes(Json(data));req.ContentLength=bytes.Length;using(var stream=req.GetRequestStream())stream.Write(bytes,0,bytes.Length);}
  using(var response=req.GetResponse())using(var stream=response.GetResponseStream())using(var memory=new MemoryStream()){var b=new byte[4096];int n;while((n=stream.Read(b,0,b.Length))>0){memory.Write(b,0,n);if(memory.Length>1048576)throw new IOException("响应过大");}return memory.ToArray();}
 }
 internal static Dictionary<string,object> Api(Dictionary<string,object> config,string path,object data){return Parse(Encoding.UTF8.GetString(Request(Text(config,"server")+path,Text(config,"pin"),Text(config,"token"),data)));}
 internal static X509Certificate2 ReadCertificate(byte[] pem){string text=Encoding.ASCII.GetString(pem).Replace("-----BEGIN CERTIFICATE-----","").Replace("-----END CERTIFICATE-----","");return new X509Certificate2(Convert.FromBase64String(text));}
 internal static void Trust(byte[] pem,string pin){var cert=ReadCertificate(pem);if(Hash(cert.RawData)!=pin)throw new InvalidOperationException("服务端证书指纹与加入码不一致");using(var store=new X509Store(StoreName.Root,StoreLocation.LocalMachine)){store.Open(OpenFlags.ReadWrite);store.Add(cert);}}
}
}
