using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Link {
internal sealed class UpdateRelease {
 internal string Version,Url,Checksums,Digest,Notes;
 internal long Size;
 internal string Tag {get{return "v"+Version;}}
 internal string FileName {get{return "Link-client-windows-amd64-"+Tag+".zip";}}
}
internal static class Updates {
 // Forks change this compile-time repository. Never accept an executable feed from a peer.
 internal const string Repository="yangToT/Link";
 internal static readonly string[] Files={"Link.exe","Uninstall.exe","uninstall.ps1","netbird.exe","wintun.dll","THIRD-PARTY-NOTICES.md","LICENSE"};
 internal static readonly string UserRoot=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Link","updates");
 const long MaxPackage=256L*1024*1024;
 internal static string ValidVersion(string version){if(version==null||version.Length>80||!Regex.IsMatch(version,@"^(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$"))throw new InvalidOperationException("版本格式不支持");return version;}
 internal static int Compare(string a,string b){
  string[] x=ValidVersion(a).Split(new[]{'-'},2),y=ValidVersion(b).Split(new[]{'-'},2);var ax=x[0].Split('.');var by=y[0].Split('.');
  for(int i=0;i<3;i++){int c=int.Parse(ax[i]).CompareTo(int.Parse(by[i]));if(c!=0)return c;}
  if(x.Length!=y.Length)return x.Length==1?1:-1;if(x.Length==1)return 0;
  ax=x[1].Split('.');by=y[1].Split('.');
  for(int i=0;i<Math.Min(ax.Length,by.Length);i++){bool n=Regex.IsMatch(ax[i],"^[0-9]+$"),m=Regex.IsMatch(by[i],"^[0-9]+$");int c=n&&m?(ax[i].TrimStart('0').Length.CompareTo(by[i].TrimStart('0').Length)):n!=m?(n?-1:1):0;if(c==0)c=string.CompareOrdinal(n?ax[i].TrimStart('0'):ax[i],m?by[i].TrimStart('0'):by[i]);if(c!=0)return c;}return ax.Length.CompareTo(by.Length);
 }
 internal static Dictionary<string,object> Preferences(){try{return Common.Parse(File.ReadAllText(Path.Combine(UserRoot,"state.json")));}catch{return Common.Map("automatic",true);}}
 internal static void SavePreferences(Dictionary<string,object> p){Directory.CreateDirectory(UserRoot);string path=Path.Combine(UserRoot,"state.json"),tmp=path+".tmp";File.WriteAllText(tmp,Common.Json(p));if(File.Exists(path))File.Replace(tmp,path,null);else File.Move(tmp,path);}
 internal static bool Due(Dictionary<string,object> p,string version,DateTime now){DateTime until;return Common.Text(p,"skipped")!=version&&(!DateTime.TryParse(Common.Text(p,"deferUntil"),null,System.Globalization.DateTimeStyles.RoundtripKind,out until)||now>=until.ToUniversalTime());}
 static bool Allowed(Uri url){return url.Scheme=="https"&&url.Port==443&&url.UserInfo==""&&(url.Host=="api.github.com"||url.Host=="github.com"||url.Host=="release-assets.githubusercontent.com");}
 static HttpWebResponse Open(string url){
  for(int i=0;i<6;i++){
   var uri=new Uri(url);if(!Allowed(uri))throw new InvalidOperationException("更新下载地址不受信任");
   var request=(HttpWebRequest)WebRequest.Create(uri);request.UserAgent="Link/"+Common.Version;request.Accept="application/vnd.github+json";request.AllowAutoRedirect=false;request.Timeout=20000;request.ReadWriteTimeout=30000;
   var response=(HttpWebResponse)request.GetResponse();int code=(int)response.StatusCode;
   if(code>=300&&code<400){url=new Uri(uri,response.Headers["Location"]).AbsoluteUri;response.Dispose();continue;}
   if(code!=200){response.Dispose();throw new InvalidOperationException("更新服务暂不可用");}return response;
  }throw new InvalidOperationException("更新下载重定向过多");
 }
 static byte[] Read(string url,int limit){using(var response=Open(url))using(var input=response.GetResponseStream())using(var output=new MemoryStream()){var buffer=new byte[8192];int n;while((n=input.Read(buffer,0,buffer.Length))>0){if(output.Length+n>limit)throw new InvalidOperationException("更新信息过大");output.Write(buffer,0,n);}return output.ToArray();}}
 internal static UpdateRelease ParseRelease(Dictionary<string,object> data,string current){
  if(Common.Bool(data,"draft"))return null;string tag=Common.Text(data,"tag_name");if(!tag.StartsWith("v"))return null;
  string version;try{version=ValidVersion(tag.Substring(1));}catch{return null;}
  if(Compare(version,current)<=0||(!current.Contains("-")&&(Common.Bool(data,"prerelease")||version.Contains("-"))))return null;
  string filename="Link-client-windows-amd64-"+tag+".zip";
  var assets=Common.Items(data,"assets").ToArray();var zip=assets.SingleOrDefault(a=>Common.Text(a,"name")==filename);var sums=assets.SingleOrDefault(a=>Common.Text(a,"name")=="SHA256SUMS.txt");
  long size;if(zip==null||sums==null||!long.TryParse(Common.Text(zip,"size"),out size)||size<1||size>MaxPackage)return null;
  string root="https://github.com/"+Repository+"/releases/download/"+tag+"/";
  if(Common.Text(zip,"browser_download_url")!=root+filename||Common.Text(sums,"browser_download_url")!=root+"SHA256SUMS.txt")return null;
  return new UpdateRelease{Version=version,Size=size,Url=root+filename,Checksums=root+"SHA256SUMS.txt",Digest=Common.Text(zip,"digest"),Notes=Common.Text(data,"body")};
 }
 internal static UpdateRelease Check(){
  string json=Encoding.UTF8.GetString(Read("https://api.github.com/repos/"+Repository+"/releases?per_page=30",1048576));
  return Common.Items(Common.Parse("{\"releases\":"+json+"}"),"releases").Select(d=>ParseRelease(d,Common.Version)).Where(r=>r!=null).OrderByDescending(r=>r.Version,Comparer<string>.Create(Compare)).FirstOrDefault();
 }
 static UpdateRelease ByVersion(string version){
  var r=ParseRelease(Common.Parse(Encoding.UTF8.GetString(Read("https://api.github.com/repos/"+Repository+"/releases/tags/v"+ValidVersion(version),1048576))),Common.Version);
  if(r==null||r.Version!=version)throw new InvalidOperationException("此版本不再提供有效更新，或版本不高于当前版本");return r;
 }
 internal static string ExpectedHash(UpdateRelease release,string sums){
  var matches=sums.Split('\n').Select(s=>Regex.Match(s.Trim(),@"^([a-fA-F0-9]{64})\s+\*?(.+)$")).Where(m=>m.Success&&m.Groups[2].Value==release.FileName).ToArray();
  if(matches.Length!=1)throw new InvalidOperationException("更新包校验信息缺失或重复");string hash=matches[0].Groups[1].Value.ToLowerInvariant();
  if(release.Digest!=""&&!string.Equals(release.Digest,"sha256:"+hash,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("发布资产与校验文件不一致");return hash;
 }
 internal static string FileHash(string file){using(var s=File.OpenRead(file))using(var sha=System.Security.Cryptography.SHA256.Create())return BitConverter.ToString(sha.ComputeHash(s)).Replace("-","").ToLowerInvariant();}
 static string HashFor(UpdateRelease r){return ExpectedHash(r,Encoding.UTF8.GetString(Read(r.Checksums,65536)));}
 internal static string Download(UpdateRelease release,Action<int> progress,CancellationToken cancel){
  Directory.CreateDirectory(UserRoot);string file=Path.Combine(UserRoot,release.FileName),partial=file+".partial";string expected=HashFor(release);
  if(File.Exists(file)&&new FileInfo(file).Length==release.Size&&FileHash(file)==expected){progress(100);return file;}
  try{
   using(var response=Open(release.Url))using(var input=response.GetResponseStream())using(var output=File.Create(partial)){
    if(response.ContentLength>=0&&response.ContentLength!=release.Size)throw new InvalidOperationException("更新包大小与发布记录不一致");
    var buffer=new byte[65536];int n;long total=0;int shown=-1;
    using(cancel.Register(()=>response.Close()))while((n=input.Read(buffer,0,buffer.Length))>0){cancel.ThrowIfCancellationRequested();total+=n;if(total>release.Size)throw new InvalidOperationException("更新包大小超出发布记录");output.Write(buffer,0,n);int percent=(int)(total*99/release.Size);if(percent!=shown){shown=percent;progress(percent);}}
    if(total!=release.Size)throw new InvalidOperationException("更新包下载不完整");
   }
   if(FileHash(partial)!=expected)throw new InvalidOperationException("更新包校验失败，请重新下载");
   if(File.Exists(file))File.Delete(file);File.Move(partial,file);progress(100);return file;
  }finally{if(File.Exists(partial))File.Delete(partial);}
 }
 internal static string[] PayloadFiles(string source){string notices=Path.Combine(source,"third_party");return Files.Concat(Directory.Exists(notices)?Directory.GetFiles(notices).Select(p=>Path.Combine("third_party",Path.GetFileName(p))):new string[0]).ToArray();}
 internal static void CheckTargets(string root,string[] names){
  root=Path.GetFullPath(root).TrimEnd('\\');
  foreach(string name in names){string target=Path.GetFullPath(Path.Combine(root,name));if(!target.StartsWith(root+"\\",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("更新路径无效");
   for(string part=target;part.Length>=root.Length;part=Path.GetDirectoryName(part)){if((File.Exists(part)||Directory.Exists(part))&&(File.GetAttributes(part)&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("更新目录包含链接，需要人工检查");if(part==root)break;}
  }
 }
 internal static void Extract(string archive,string target,string version){
  // Fixed root files only: no scripts, paths or executable list controlled by the archive.
  using(var zip=ZipFile.OpenRead(archive)){
   long expanded=0;var entries=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
   foreach(var item in zip.Entries){expanded+=item.Length;if(expanded>512L*1024*1024||item.Length<0||!entries.Add(item.FullName)||item.FullName.Contains("\\")||item.FullName.StartsWith("/")||item.FullName.Split('/').Any(p=>p==".."||p.Contains(':')))throw new InvalidOperationException("更新包结构无效");}
   var manifest=zip.GetEntry("client-update.json");if(manifest==null||manifest.Length>8192)throw new InvalidOperationException("更新包不支持自动安装");
   Dictionary<string,object> metadata;using(var reader=new StreamReader(manifest.Open()))metadata=Common.Parse(reader.ReadToEnd());
   if(Common.Text(metadata,"version")!=version||Common.Text(metadata,"format")!="1"||Compare(Common.Text(metadata,"minimumUpdater"),Common.Version)>0)throw new InvalidOperationException("更新格式不兼容，请手动安装此版本");
   Directory.CreateDirectory(target);
   foreach(string name in Files){var item=zip.GetEntry(name);if(item==null||item.Length==0)throw new InvalidOperationException("更新包缺少 "+name);using(var input=item.Open())using(var output=new FileStream(Path.Combine(target,name),FileMode.CreateNew))input.CopyTo(output);}
   foreach(var item in zip.Entries.Where(e=>e.FullName.StartsWith("third_party/",StringComparison.Ordinal)&&!e.FullName.EndsWith("/"))){if(item.FullName.Split('/').Length!=2)throw new InvalidOperationException("第三方声明路径无效");string dir=Path.Combine(target,"third_party");Directory.CreateDirectory(dir);using(var input=item.Open())using(var output=new FileStream(Path.Combine(dir,Path.GetFileName(item.FullName)),FileMode.CreateNew))input.CopyTo(output);}
  }
 }
 internal static string JobPath(string id){if(!Regex.IsMatch(id??"","^[a-f0-9]{32}$"))throw new InvalidOperationException("更新任务无效");return Path.Combine(Common.Home,"updates",id);}
 internal static void Prepare(string version,string file,string id,int parent,long ticks){
  if(!Components.Administrator)throw new InvalidOperationException("更新需要管理员权限");Common.ProtectFolder();string job=JobPath(id);
  using(var gui=Process.GetProcessById(parent)){if(gui.StartTime.ToUniversalTime().Ticks!=ticks||Path.GetFileName(gui.MainModule.FileName)!="Link.exe")throw new InvalidOperationException("原窗口已变化，请重新发起更新");}
  var release=ByVersion(version);string expected=HashFor(release);if(Directory.Exists(job))throw new InvalidOperationException("更新任务已存在，请重试");Directory.CreateDirectory(job);
  var acl=new DirectorySecurity();acl.SetAccessRuleProtection(true,false);
  foreach(var sid in new[]{WellKnownSidType.LocalSystemSid,WellKnownSidType.BuiltinAdministratorsSid})acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid,null),FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
  acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid,null),FileSystemRights.ReadAndExecute,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));Directory.SetAccessControl(job,acl);
  string archive=Path.Combine(job,"package.zip");using(var source=File.OpenRead(file))using(var output=new FileStream(archive,FileMode.CreateNew)){if(source.Length!=release.Size)throw new InvalidOperationException("下载文件大小已改变");source.CopyTo(output);}
  if(FileHash(archive)!=expected)throw new InvalidOperationException("下载文件已变化，拒绝安装");
  Extract(archive,Path.Combine(job,"payload"),version);
  File.Copy(System.Reflection.Assembly.GetExecutingAssembly().Location,Path.Combine(job,"worker.exe"),false);
  File.WriteAllText(Path.Combine(job,"job.json"),Common.Json(Common.Map("version",version,"parent",parent,"ticks",ticks)));
 }
 internal static void StartWorker(string id){string job=JobPath(id);
  using(var worker=Process.Start(new ProcessStartInfo(Path.Combine(job,"worker.exe"),"--apply-update "+id){UseShellExecute=false,CreateNoWindow=true})){}
 }
 internal static void Apply(string id){
  string job=JobPath(id);if(!Components.Administrator||!Path.GetFullPath(Common.Bin).TrimEnd('\\').Equals(job,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("更新进程路径无效");
  bool ok=ProgressWindow.Run("更新 Link",report=>{
   try{
   var info=Common.Parse(File.ReadAllText(Path.Combine(job,"job.json")));report("正在等待原窗口退出…");
   try{using(var parent=Process.GetProcessById(int.Parse(Common.Text(info,"parent"))))if(parent.StartTime.ToUniversalTime().Ticks==long.Parse(Common.Text(info,"ticks"))&&!parent.WaitForExit(90000))throw new InvalidOperationException("原窗口尚未退出，本次未安装");}catch(ArgumentException){}
   report("正在清理 Link 连接、备份并更新程序…");
   Program.InstallFrom(Path.Combine(job,"payload"),Common.Text(info,"version"),()=>{
    report("正在验证新版本后台…");
    for(int i=0;i<15;i++){try{if(Common.Text(Common.Pipe(Common.Map("action","status")),"version")==Common.Text(info,"version"))return;}catch{}Thread.Sleep(1000);}throw new InvalidOperationException("新版本后台验证失败，正在回退");
   });
   File.WriteAllText(Path.Combine(job,"result.txt"),"SUCCESS");
   }catch(Exception error){File.WriteAllText(Path.Combine(job,"update-error.txt"),error.GetType().Name+": "+error.Message);File.WriteAllText(Path.Combine(job,"result.txt"),"FAILED");throw;}
  },"Link 已更新，正在重新打开窗口",Path.Combine(job,"update-error.txt"),true);
  if(!ok){File.WriteAllText(Path.Combine(job,"result.txt"),"FAILED");Environment.ExitCode=1;}
 }
 internal static void Watch(string id){
  string job=JobPath(id),result=Path.Combine(job,"result.txt");for(int i=0;i<360;i++){if(File.Exists(result))break;Thread.Sleep(1000);}
  Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"Link","Link.exe")){UseShellExecute=true});
 }
}

internal sealed partial class MainWindow {
 Dictionary<string,object> updatePreferences=Updates.Preferences();UpdateRelease availableUpdate;string downloadedUpdate="";bool checkingUpdate,downloadingUpdate;DateTime nextUpdateCheck=DateTime.MinValue;CancellationTokenSource updateCancel;DispatcherTimer updateTimer;
 readonly StackPanel updateBanner=new StackPanel{Margin=new Thickness(0,0,0,8)};
 void StartUpdates(){
  string last=Common.Text(updatePreferences,"lastJob");if(last!="")try{string result=Path.Combine(Updates.JobPath(last),"result.txt");if(File.Exists(result)){bool ok=File.ReadAllText(result)=="SUCCESS";Feedback(ok?"Link 已更新，设备身份和设置已保留。":"上次更新未完成，请查看更新窗口中的失败原因。旧版本恢复记录已保留。",!ok);updatePreferences.Remove("lastJob");Updates.SavePreferences(updatePreferences);}}catch{}
  updateTimer=new DispatcherTimer{Interval=TimeSpan.FromMinutes(1)};updateTimer.Tick+=async(s,e)=>await CheckUpdates(false);updateTimer.Start();
  Dispatcher.BeginInvoke((Action)(async()=>await CheckUpdates(false)));
 }
 async Task CheckUpdates(bool manual){
  if(preview||checkingUpdate||downloadingUpdate||busy||actionPending)return;
  if(!manual&&(!Common.Bool(updatePreferences,"automatic")||DateTime.UtcNow<nextUpdateCheck))return;
  checkingUpdate=true;nextUpdateCheck=DateTime.UtcNow.AddHours(6);if(manual)Feedback("正在检查更新…");
  try{var release=await Task.Run(()=>Updates.Check());availableUpdate=release;if(release==null){if(manual)Feedback("当前已是此通道最新版本");updateBanner.Children.Clear();return;}
   string cached=Path.Combine(Updates.UserRoot,release.FileName);downloadedUpdate=File.Exists(cached)&&Common.Text(updatePreferences,"downloaded")==release.Version?cached:"";
   if(manual||Updates.Due(updatePreferences,release.Version,DateTime.UtcNow)){ShowUpdate();if(!IsVisible&&tray!=null)tray.ShowBalloonTip(5000,"Link 有新版本",release.Version+" 已发布，打开 Link 选择更新时间。",System.Windows.Forms.ToolTipIcon.Info);}
  }catch{if(manual)Feedback("暂时无法检查更新，请稍后重试。可在 GitHub Releases 查看版本。",true);}finally{checkingUpdate=false;}
 }
 Button UpdateButton(string label,Func<Task> action){var button=new Button{Content=label};StyleButton(button);button.Margin=new Thickness(0,0,6,0);button.Click+=async(s,e)=>{if(!button.IsEnabled)return;button.IsEnabled=false;try{await action();}catch(Exception error){Feedback(error.Message,true);}finally{button.IsEnabled=true;}};return button;}
 void ShowUpdate(){
  updateBanner.Children.Clear();if(availableUpdate==null)return;
  var title=new TextBlock{Text=downloadedUpdate==""?"发现新版本 "+availableUpdate.Version:"更新已下载 · "+availableUpdate.Version,FontWeight=FontWeights.SemiBold};updateBanner.Children.Add(title);
  updateBanner.Children.Add(new TextBlock{Text=downloadedUpdate==""?"选择更新后下载，安装前仍可继续使用。":"重启 Link 后生效，连接会短暂中断。无需重启电脑。",Foreground=muted,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,5,0,5)});
  var actions=new WrapPanel();updateBanner.Children.Add(actions);
  actions.Children.Add(UpdateButton(downloadedUpdate==""?"立即更新":"立即重启",async()=>{if(downloadedUpdate=="")await DownloadUpdate();else await RestartForUpdate();}));
  actions.Children.Add(Button(downloadedUpdate==""?"稍后更新":"稍后重启",()=>{updatePreferences["deferUntil"]=DateTime.UtcNow.AddHours(24).ToString("o");Updates.SavePreferences(updatePreferences);updateBanner.Children.Clear();Feedback("已推迟 24 小时；仍可在设置中检查更新。");}));
  if(downloadedUpdate=="")actions.Children.Add(Button("跳过此版本",()=>{updatePreferences["skipped"]=availableUpdate.Version;Updates.SavePreferences(updatePreferences);updateBanner.Children.Clear();}));
  actions.Children.Add(Button("版本说明",()=>Process.Start(new ProcessStartInfo("https://github.com/"+Updates.Repository+"/releases/tag/"+availableUpdate.Tag){UseShellExecute=true})));
 }
 async Task DownloadUpdate(){
  if(downloadingUpdate||busy||actionPending)return;downloadingUpdate=true;updateCancel=new CancellationTokenSource();var cancel=updateCancel;
  updateBanner.Children.Clear();var label=new TextBlock{Text="正在下载更新…"};var progress=new ProgressBar{Minimum=0,Maximum=100,Height=6,Margin=new Thickness(0,8,0,8)};updateBanner.Children.Add(label);updateBanner.Children.Add(progress);updateBanner.Children.Add(Button("取消下载",()=>cancel.Cancel()));
  try{var release=availableUpdate;downloadedUpdate=await Task.Run(()=>Updates.Download(release,n=>Dispatcher.BeginInvoke((Action)(()=>{progress.Value=n;label.Text="正在下载更新 · "+n+"%";})),cancel.Token));updatePreferences["downloaded"]=release.Version;updatePreferences["skipped"]="";updatePreferences["deferUntil"]="";Updates.SavePreferences(updatePreferences);ShowUpdate();if(!IsVisible&&tray!=null)tray.ShowBalloonTip(5000,"Link 更新已下载","打开 Link 选择立即重启或稍后重启。",System.Windows.Forms.ToolTipIcon.Info);}
  catch(Exception){ShowUpdate();Feedback(cancel.IsCancellationRequested?"下载已取消，可随时重试。":"下载或校验未完成，当前版本继续运行，请重试。",!cancel.IsCancellationRequested);}finally{downloadingUpdate=false;updateCancel=null;cancel.Dispose();}
 }
 async Task RestartForUpdate(){
  if(busy||actionPending||downloadingUpdate)return;busy=true;Feedback("正在验证更新，Windows 将请求管理员权限…");
  string id=Guid.NewGuid().ToString("N");
  try{
   int pid=Process.GetCurrentProcess().Id;long ticks=Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
   await Task.Run(()=>Components.Elevate("--prepare-update "+availableUpdate.Version+" "+Common.Quote(downloadedUpdate)+" "+id+" "+pid+" "+ticks));
   updatePreferences["lastJob"]=id;Updates.SavePreferences(updatePreferences);
   string watcher=Path.Combine(Updates.UserRoot,"watch-"+id+".exe");File.Copy(System.Reflection.Assembly.GetExecutingAssembly().Location,watcher);
   Process.Start(new ProcessStartInfo(watcher,"--watch-update "+id){UseShellExecute=false,CreateNoWindow=true});PrepareExit();Close();
  }catch(Exception e){Feedback(e is System.ComponentModel.Win32Exception?"已取消更新，当前连接保留。":e.Message,true);}finally{busy=false;}
 }
 internal void RenderUpdateImage(bool ready){activePage="settings";availableUpdate=new UpdateRelease{Version="0.2.0-alpha.99"};downloadedUpdate=ready?"example.zip":"";ShowUpdate();RenderImage();File.Copy(Path.Combine(Common.Bin,"client-render.png"),Path.Combine(Common.Bin,ready?"update-ready.png":"update-available.png"),true);}
 void RenderUpdateSettings(){
  body.Children.Add(new TextBlock{Text="软件更新",FontSize=17,Margin=new Thickness(0,22,0,10)});
  var auto=new CheckBox{Content="自动检查更新（只提醒，不自动安装）",IsChecked=Common.Bool(updatePreferences,"automatic"),Margin=new Thickness(0,6,0,10)};
  auto.Click+=(s,e)=>{updatePreferences["automatic"]=auto.IsChecked==true;Updates.SavePreferences(updatePreferences);};body.Children.Add(auto);
  body.Children.Add(UpdateButton("检查更新",async()=>await CheckUpdates(true)));
  body.Children.Add(Label("当前版本："+Common.Version+" · "+(Common.Version.Contains("-")?"预览通道":"稳定通道")));
 }
}
}
