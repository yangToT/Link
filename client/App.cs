using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Link {
internal static class Program {
 [STAThread] public static void Main(string[] args){try{Start(args);}catch(Exception e){File.WriteAllText(Path.Combine(Common.Bin,"startup-error.txt"),e.GetType().FullName+"\n"+e.Message+"\n"+e.StackTrace);Environment.ExitCode=1;}}
 static void Start(string[] args){
  ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls12;
  if(args.Contains("--job-child")){System.Threading.Thread.Sleep(30000);return;}
  if(args.Contains("--service")){ServiceBase.Run(new Agent());return;}
  if(args.Contains("--self-test")){SelfTest.Run();return;}
  if(args.Contains("--tray-test")){SelfTest.Tray();return;}
  if(args.Contains("--network-check")){Console.WriteLine(Common.Json(NetworkDiscovery.Discover("")));return;}
  if(args.Contains("--cleanup-layer2")){Common.ProtectFolder();var layer=new Layer2();layer.Recover();if(Common.Text(layer.Status,"state")=="cleanup-failed")throw new InvalidOperationException("二层资源清理未完成，保留恢复记录");return;}
  if(args.Length==2&&args[0]=="--check-server"){var config=Common.Parse(File.ReadAllText(args[1]));Common.Request(Common.Text(config,"server")+"/health",Common.Text(config,"pin"),"",null);bool rejected=false;try{Common.Request(Common.Text(config,"server")+"/health",new string('0',64),"",null);}catch(System.Net.WebException){rejected=true;}if(!rejected)throw new Exception("Wrong CA pin accepted");Console.WriteLine("PASS: real server pinned TLS; wrong fingerprint rejected");return;}
  if(args.Contains("--render")){var window=new MainWindow(true);window.RenderImage();return;}
  if(args.Contains("--preview")){new Application().Run(new MainWindow(true));return;}
  using(var restore=new System.Threading.EventWaitHandle(false,System.Threading.EventResetMode.AutoReset,"Local\\Link.Client.Restore")){
   bool first;using(var instance=new System.Threading.Mutex(true,"Local\\Link.Client.Window",out first)){
    if(!first){restore.Set();return;}
    var app=new Application();var window=new MainWindow(false);
    var wait=System.Threading.ThreadPool.RegisterWaitForSingleObject(restore,(s,t)=>{if(!app.Dispatcher.HasShutdownStarted)app.Dispatcher.BeginInvoke((Action)window.RestoreWindow);},null,-1,false);
    app.SessionEnding+=(s,e)=>window.PrepareExit();
    try{app.Run(window);}finally{wait.Unregister(null);instance.ReleaseMutex();}
   }
  }
 }
 internal static void Install(){
  string directory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"Link");Directory.CreateDirectory(directory);
  foreach(string name in new[]{"Link.exe","Uninstall.exe","uninstall.ps1","netbird.exe","wintun.dll","THIRD-PARTY-NOTICES.md","LICENSE"}){
   string source=Path.Combine(Common.Bin,name);if(!File.Exists(source)){if(name.EndsWith(".exe"))throw new InvalidOperationException("安装包缺少 "+name);continue;}
   string target=Path.Combine(directory,name);if(!source.Equals(target,StringComparison.OrdinalIgnoreCase))File.Copy(source,target,true);
  }
  Common.ProtectFolder();bool exists=ServiceController.GetServices().Any(s=>s.ServiceName=="LinkAgent");
  File.WriteAllText(Path.Combine(Common.Home,"install-owned.json"),Common.Json(Common.Map("program",directory,"version",Common.Version)));
  using(var uninstall=Microsoft.Win32.Registry.LocalMachine.CreateSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Link.Client")){
   uninstall.SetValue("DisplayName","Link");uninstall.SetValue("DisplayVersion",Common.Version);uninstall.SetValue("InstallLocation",directory);uninstall.SetValue("DisplayIcon",Path.Combine(directory,"Link.exe"));uninstall.SetValue("UninstallString",Common.Quote(Path.Combine(directory,"Uninstall.exe")));uninstall.SetValue("NoModify",1);uninstall.SetValue("NoRepair",1);
  }
  if(!exists)Common.Run("sc.exe","create LinkAgent binPath= \"\\\""+Path.Combine(directory,"Link.exe")+"\\\" --service\" start= auto DisplayName= "+Common.Quote("Link 后台连接"));
  using(var service=new ServiceController("LinkAgent")){if(service.Status!=ServiceControllerStatus.Running){service.Start();service.WaitForStatus(ServiceControllerStatus.Running,TimeSpan.FromSeconds(20));}}
  Common.Run("sc.exe","failure LinkAgent reset= 86400 actions= restart/5000/restart/15000/restart/60000");
 }
}
internal sealed class MainWindow : Window {
 readonly bool preview;readonly StackPanel body=new StackPanel();readonly TextBlock status=new TextBlock(),entry=new TextBlock(),feedback=new TextBlock();readonly Border entryBox=new Border();
 readonly Button connection=new Button(),management=new Button();readonly TextBox server=new TextBox(),code=new TextBox();readonly CheckBox autoStart=new CheckBox(),autoConnect=new CheckBox();
 Dictionary<string,object> snapshot=Common.Map();bool busy,openOnConnect,refreshing;string activePage="devices";DispatcherTimer timer;
 System.Windows.Forms.NotifyIcon tray;System.Drawing.Icon trayIcon;System.Windows.Forms.ToolStripMenuItem trayManagement;bool exiting,notified;WindowState restoredState=WindowState.Normal;
 readonly Brush ink=new SolidColorBrush(Color.FromRgb(35,46,51)),muted=new SolidColorBrush(Color.FromRgb(110,120,125)),accent=new SolidColorBrush(Color.FromRgb(34,113,92));
 internal MainWindow(bool isPreview){
  preview=isPreview;Title="Link";Width=820;Height=610;MinWidth=670;MinHeight=520;WindowStartupLocation=WindowStartupLocation.CenterScreen;FontFamily=new FontFamily("Segoe UI, Microsoft YaHei UI");FontSize=13;Foreground=ink;
  using(var icon=System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("Link.AppIcon")){Icon=System.Windows.Media.Imaging.BitmapFrame.Create(icon,System.Windows.Media.Imaging.BitmapCreateOptions.None,System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);}
  Background=new SolidColorBrush(Color.FromArgb(224,240,245,244));
  var root=new Grid{Background=Background};root.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(192)});root.ColumnDefinitions.Add(new ColumnDefinition());Content=root;
  var rail=new DockPanel{Margin=new Thickness(22,28,18,20)};Grid.SetColumn(rail,0);root.Children.Add(rail);
  var brand=new TextBlock{Text="Link",FontSize=27,FontWeight=FontWeights.SemiBold,Margin=new Thickness(8,0,0,32)};DockPanel.SetDock(brand,Dock.Top);rail.Children.Add(brand);
  var foot=new TextBlock{Text="SELF-HOSTED\n"+Common.Version,Foreground=muted,FontSize=10,LineHeight=19,Margin=new Thickness(8)};DockPanel.SetDock(foot,Dock.Bottom);rail.Children.Add(foot);
  var navigation=new StackPanel();rail.Children.Add(navigation);
  foreach(var page in new[]{new[]{"devices","设备"},new[]{"services","服务"},new[]{"settings","设置"}}){string key=page[0];var b=Button(page[1],()=>{activePage=key;Render();});b.HorizontalContentAlignment=HorizontalAlignment.Left;b.Margin=new Thickness(0,3,0,3);navigation.Children.Add(b);}
  var main=new Grid{Margin=new Thickness(16,28,30,22)};Grid.SetColumn(main,1);root.Children.Add(main);main.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});main.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});main.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
  var top=new DockPanel{Margin=new Thickness(0,0,0,20)};main.Children.Add(top);connection.Content="连接";StyleButton(connection);connection.Click+=async(s,e)=>await Connect();DockPanel.SetDock(connection,Dock.Right);top.Children.Add(connection);
  var headline=new StackPanel();headline.Children.Add(new TextBlock{Text="我的网络",FontSize=22,FontWeight=FontWeights.SemiBold});status.Text="未连接";status.Foreground=muted;status.Margin=new Thickness(0,7,0,0);headline.Children.Add(status);top.Children.Add(headline);
  var scroll=new ScrollViewer{VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Content=body};Grid.SetRow(scroll,1);main.Children.Add(scroll);
  var bottom=new StackPanel();Grid.SetRow(bottom,2);main.Children.Add(bottom);feedback.Foreground=muted;feedback.TextWrapping=TextWrapping.Wrap;feedback.Margin=new Thickness(0,8,0,8);bottom.Children.Add(feedback);
  management.Content="打开管理中心  ↗";StyleButton(management);management.HorizontalAlignment=HorizontalAlignment.Stretch;management.Click+=async(s,e)=>await OpenManagement();bottom.Children.Add(management);
  SourceInitialized+=(s,e)=>Glass();Loaded+=async(s,e)=>{Render();if(!preview){InitializeTray();await Refresh();timer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(3)};timer.Tick+=async(a,b)=>await Refresh();timer.Start();}};
  StateChanged+=(s,e)=>{if(WindowState==WindowState.Minimized&&tray!=null)HideToTray();else if(WindowState!=WindowState.Minimized)restoredState=WindowState;};
  Closing+=(s,e)=>{if(!exiting&&tray!=null){e.Cancel=true;HideToTray();}};
  Closed+=(s,e)=>{if(timer!=null)timer.Stop();if(tray!=null){tray.Visible=false;tray.ContextMenuStrip.Dispose();tray.Dispose();tray=null;}if(trayIcon!=null)trayIcon.Dispose();};
 }
 internal void InitializeTray(){
  if(tray!=null)return;
  using(var stream=System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("Link.AppIcon"))using(var source=new System.Drawing.Icon(stream)){trayIcon=new System.Drawing.Icon(source,System.Windows.Forms.SystemInformation.SmallIconSize);}
  var menu=new System.Windows.Forms.ContextMenuStrip();
  menu.Items.Add("打开 Link",null,(s,e)=>RestoreWindow());
  trayManagement=new System.Windows.Forms.ToolStripMenuItem("打开管理中心",null,async(s,e)=>await OpenManagement()){Enabled=management.IsEnabled};menu.Items.Add(trayManagement);
  menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());menu.Items.Add("退出界面（保持连接）",null,(s,e)=>{PrepareExit();Close();});
  tray=new System.Windows.Forms.NotifyIcon{Icon=trayIcon,Text="Link · "+status.Text,ContextMenuStrip=menu,Visible=true};
  tray.DoubleClick+=(s,e)=>RestoreWindow();
 }
 internal void PrepareExit(){exiting=true;}
 internal void RestoreWindow(){Show();WindowState=restoredState;Activate();Focus();}
 void HideToTray(){Hide();if(!notified){notified=true;tray.ShowBalloonTip(3000,"Link 已收起","双击托盘图标可恢复窗口，后台连接保持运行。",System.Windows.Forms.ToolTipIcon.Info);}}
 internal bool TrayVisible {get{return tray!=null&&tray.Visible;}}
 void StyleButton(Button b){b.Padding=new Thickness(13,9,13,9);b.Background=new SolidColorBrush(Color.FromArgb(160,255,255,255));b.BorderBrush=new SolidColorBrush(Color.FromArgb(40,100,125,120));b.BorderThickness=new Thickness(1);b.Cursor=System.Windows.Input.Cursors.Hand;b.Foreground=ink;
  var border=new FrameworkElementFactory(typeof(Border));border.SetValue(Border.CornerRadiusProperty,new CornerRadius(7));border.SetValue(Border.BackgroundProperty,new TemplateBindingExtension(Control.BackgroundProperty));border.SetValue(Border.PaddingProperty,new TemplateBindingExtension(Control.PaddingProperty));
  var content=new FrameworkElementFactory(typeof(ContentPresenter));content.SetValue(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Center);content.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);border.AppendChild(content);var template=new ControlTemplate(typeof(Button)){VisualTree=border};
  var hover=new Trigger{Property=UIElement.IsMouseOverProperty,Value=true};hover.Setters.Add(new Setter(Control.BackgroundProperty,new SolidColorBrush(Color.FromArgb(210,222,235,231))));template.Triggers.Add(hover);
  var disabled=new Trigger{Property=UIElement.IsEnabledProperty,Value=false};disabled.Setters.Add(new Setter(UIElement.OpacityProperty,0.5));template.Triggers.Add(disabled);b.Template=template;
 }
 Button Button(string label,Action click){var b=new Button{Content=label};StyleButton(b);b.Click+=(s,e)=>click();return b;}
 TextBlock Label(string text){return new TextBlock{Text=text,Foreground=muted,Margin=new Thickness(0,15,0,8)};}
 Border Card(UIElement child){return new Border{Background=new SolidColorBrush(Color.FromArgb(135,255,255,255)),CornerRadius=new CornerRadius(9),BorderBrush=new SolidColorBrush(Color.FromArgb(90,255,255,255)),BorderThickness=new Thickness(1),Padding=new Thickness(17),Margin=new Thickness(0,0,0,10),Child=child};}
 void Render(){
  body.Children.Clear();var state=Common.Obj(snapshot,"state")??Common.Map();var self=Common.Obj(state,"device");bool registered=Common.Bool(snapshot,"registered"),online=Common.Bool(self,"connected");
  status.Text=preview?"未连接":Common.Text(snapshot,"message","后台服务未安装");connection.Content=Common.Bool(snapshot,"wanted")?"断开":"连接";connection.IsEnabled=!busy;management.IsEnabled=online&&Common.Text(self,"role")=="admin";
  if(tray!=null){string tooltip="Link · "+status.Text;tray.Text=tooltip.Length>63?tooltip.Substring(0,63):tooltip;trayManagement.Enabled=management.IsEnabled;}
  string entryID=Common.Text(state,"entryId");if(self!=null&&entryID==Common.Text(self,"id")){body.Children.Add(Card(new TextBlock{Text="●  本机是网络入口",Foreground=accent,FontWeight=FontWeights.SemiBold}));}
  var networkInfo=Common.Obj(snapshot,"network");string warning=Common.Text(networkInfo,"warning");if(warning!="")body.Children.Add(Label(warning));
  if(Common.Text(state,"networkMode")=="bridged"){
   var bridge=Common.Obj(snapshot,"layer2");body.Children.Add(Card(new TextBlock{Text="局域网接入 · "+Common.Text(bridge,"message","准备中")+"\n"+Common.Text(bridge,"ip"),TextWrapping=TextWrapping.Wrap}));
  }
  if(activePage=="settings"){
   body.Children.Add(new TextBlock{Text="连接设置",FontSize=17,Margin=new Thickness(0,0,0,15)});
   autoStart.Content="开机启动后台连接";autoStart.IsChecked=Common.Bool(snapshot,"autoStart");autoStart.Margin=new Thickness(0,12,0,12);body.Children.Add(autoStart);
   autoConnect.Content="后台服务启动后自动连接";autoConnect.IsChecked=Common.Bool(snapshot,"autoConnect");autoConnect.Margin=new Thickness(0,12,0,12);body.Children.Add(autoConnect);
   body.Children.Add(Label("入口与隧道使用的物理网卡"));var adapterChoice=new ComboBox{Margin=new Thickness(0,0,0,12)};adapterChoice.Items.Add(new ComboBoxItem{Content="自动选择（多网卡时需手动指定）",Tag=""});adapterChoice.SelectedIndex=0;
   foreach(var adapter in Common.Items(networkInfo,"adapters")){var item=new ComboBoxItem{Content=Common.Text(adapter,"name")+" · "+Common.Text(adapter,"ip")+(Common.Text(adapter,"kind")=="wifi"?" · Wi-Fi":""),Tag=Common.Text(adapter,"id")};adapterChoice.Items.Add(item);if(Common.Text(adapter,"id")==Common.Text(snapshot,"entryAdapterId"))adapterChoice.SelectedItem=item;}body.Children.Add(adapterChoice);
   if(Common.Bool(networkInfo,"tunDetected"))body.Children.Add(Label("检测到代理 / TUN；局域网接入会检查隧道出口，不修改代理配置。"));
   body.Children.Add(Button("保存设置",async()=>await Execute(Common.Map("action","settings","autoStart",autoStart.IsChecked==true,"autoConnect",autoConnect.IsChecked==true,"entryAdapterId",Convert.ToString(((ComboBoxItem)adapterChoice.SelectedItem).Tag)))));
   body.Children.Add(new TextBlock{Text="最小化或关闭窗口会收起到托盘，双击托盘图标可恢复。\n退出界面后，连接仍由后台服务维持。\n主动断开或被踢下线后，需要手动连接。",Foreground=muted,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,22,0,0)});return;
  }
  if(!registered){
   body.Children.Add(new TextBlock{Text="加入一个网络",FontSize=17,Margin=new Thickness(0,0,0,4)});body.Children.Add(new TextBlock{Text="输入自托管实例的地址与一次性加入码。",Foreground=muted});body.Children.Add(Label("服务端地址"));
   server.Padding=new Thickness(10);if(server.Text=="")server.Text="https://";body.Children.Add(server);body.Children.Add(Label("一次性加入码"));code.Padding=new Thickness(10);code.TextWrapping=TextWrapping.Wrap;code.MinHeight=62;body.Children.Add(code);
   body.Children.Add(new TextBlock{Text="首次连接将安装后台组件与此实例的证书。",Foreground=muted,Margin=new Thickness(0,13,0,18),TextWrapping=TextWrapping.Wrap});body.Children.Add(Button("加入并连接",async()=>await Enroll()));return;
  }
  if(activePage=="services"){
   body.Children.Add(new TextBlock{Text="本机服务",FontSize=17,Margin=new Thickness(0,0,0,16)});
   if(Common.Text(state,"networkMode")=="bridged"){var layer=Common.Obj(snapshot,"layer2");body.Children.Add(Label(Common.Text(layer,"message")));body.Children.Add(Label("局域网地址："+Common.Text(layer,"ip","等待分配")));body.Children.Add(Label("通过局域网地址和应用自身端口访问，无需添加映射。\n应用监听地址与系统防火墙仍需允许访问。"));return;}
   var mappings=Common.Items(state,"mappings").Where(m=>Common.Text(m,"deviceId")==Common.Text(self,"id")).ToList();
   if(mappings.Count==0)body.Children.Add(Label("暂无已发布服务，请在管理中心添加映射。"));
   foreach(var m in mappings){var stack=new StackPanel();stack.Children.Add(new TextBlock{Text=Common.Text(m,"name"),FontWeight=FontWeights.SemiBold});stack.Children.Add(Label("本机 TCP "+Common.Text(m,"port")+"  ·  "+MappingStatus(Common.Text(m,"state"))));body.Children.Add(Card(stack));}return;
  }
  body.Children.Add(new TextBlock{Text="设备",FontSize=17,Margin=new Thickness(0,0,0,15)});
  foreach(var d in Common.Items(state,"devices")){
   var row=new DockPanel();bool isSelf=Common.Text(d,"id")==Common.Text(self,"id");string ip=Common.Text(d,"ip");
   if(!isSelf){var remote=Button("远程桌面",()=>{IPAddress parsed;if(IPAddress.TryParse(ip,out parsed))Process.Start("mstsc.exe","/v:"+ip);});remote.IsEnabled=Common.Bool(d,"connected");DockPanel.SetDock(remote,Dock.Right);row.Children.Add(remote);}
   var text=new StackPanel();text.Children.Add(new TextBlock{Text=Common.Text(d,"name")+(isSelf?"  ·  本机":""),FontWeight=FontWeights.SemiBold});text.Children.Add(new TextBlock{Text=(Common.Bool(d,"connected")?"● 在线":"○ 离线")+"    "+ip+(Common.Text(d,"id")==entryID?"    网络入口":""),Foreground=muted,Margin=new Thickness(0,8,0,0),TextWrapping=TextWrapping.Wrap});row.Children.Add(text);body.Children.Add(Card(row));
  }
  if(!online)body.Children.Add(Label("设备列表将在网络连接成功后更新。"));
 }
 static string MappingStatus(string s){return s=="ready"?"可用":s=="error"?"转发失败":s=="offline"?"设备离线":"等待同步";}
 async Task Refresh(){if(busy||refreshing||preview)return;refreshing=true;try{var next=await Task.Run(()=>Common.Pipe(Common.Map("action","status")));snapshot=next;if(activePage!="settings")Render();}catch{status.Text="后台服务未安装或未启动";}finally{refreshing=false;}if(openOnConnect&&management.IsEnabled){openOnConnect=false;await OpenManagement();}}
 async Task Execute(Dictionary<string,object> request){if(busy||preview)return;busy=true;connection.IsEnabled=false;try{await Task.Run(()=>Common.Pipe(request));feedback.Text="";}catch(Exception e){feedback.Text=e.Message;}finally{busy=false;}await Refresh();}
 async Task Connect(){if(!Common.Bool(snapshot,"registered")){await Enroll();return;}await Execute(Common.Map("action",Common.Bool(snapshot,"wanted")?"disconnect":"connect"));}
 async Task Enroll(){if(busy||preview)return;string endpoint=server.Text,credential=code.Text;if(credential.Trim()==""){feedback.Text="请输入一次性加入码";return;}busy=true;connection.IsEnabled=false;feedback.Text="正在准备后台服务…";
  try{await Task.Run(()=>Program.Install());await Task.Run(()=>Common.Pipe(Common.Map("action","enroll","server",endpoint,"code",credential)));code.Clear();feedback.Text="已加入，正在建立连接。";openOnConnect=true;}
  catch(Exception e){feedback.Text=e.Message;}finally{busy=false;}await Refresh();
 }
 async Task OpenManagement(){if(busy||preview)return;try{var reply=await Task.Run(()=>Common.Pipe(Common.Map("action","browser")));string url=Common.Text(reply,"url");Uri uri;if(!Uri.TryCreate(url,UriKind.Absolute,out uri)||uri.Scheme!="https")throw new InvalidOperationException("管理地址无效");Process.Start(new ProcessStartInfo(url){UseShellExecute=true});}catch(Exception e){feedback.Text=e.Message;}}
 [StructLayout(LayoutKind.Sequential)]struct Accent {public int State,Flags,Color,Animation;}
 [StructLayout(LayoutKind.Sequential)]struct Composition {public int Attribute;public IntPtr Data;public int Size;}
 [DllImport("user32.dll")]static extern int SetWindowCompositionAttribute(IntPtr handle,ref Composition data);
 [StructLayout(LayoutKind.Sequential)]struct Margins {public int Left,Right,Top,Bottom;}
 [DllImport("dwmapi.dll")]static extern int DwmExtendFrameIntoClientArea(IntPtr window,ref Margins margins);
 void Glass(){try{var handle=new WindowInteropHelper(this).Handle;HwndSource.FromHwnd(handle).CompositionTarget.BackgroundColor=Colors.Transparent;var margins=new Margins{Left=-1};DwmExtendFrameIntoClientArea(handle,ref margins);var accent=new Accent{State=4,Color=unchecked((int)0xccf3f7f5)};var pointer=Marshal.AllocHGlobal(Marshal.SizeOf(accent));try{Marshal.StructureToPtr(accent,pointer,false);var data=new Composition{Attribute=19,Data=pointer,Size=Marshal.SizeOf(accent)};SetWindowCompositionAttribute(handle,ref data);}finally{Marshal.FreeHGlobal(pointer);}}catch{Background=new SolidColorBrush(Color.FromRgb(240,245,244));}}
 internal void RenderImage(){Render();var root=(FrameworkElement)Content;root.Width=820;root.Height=570;root.Measure(new Size(820,570));root.Arrange(new Rect(0,0,820,570));root.UpdateLayout();var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap(820,570,96,96,PixelFormats.Pbgra32);bitmap.Render(root);var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));using(var file=File.Create(Path.Combine(Common.Bin,"client-render.png")))encoder.Save(file);}
}
internal static class SelfTest {
 internal static void Tray(){
  var app=new Application();var window=new MainWindow(true);
  window.Loaded+=(s,e)=>window.Dispatcher.BeginInvoke((Action)(()=>{
   try{
    window.InitializeTray();if(window.Icon==null||!window.TrayVisible)throw new Exception("Application or tray icon missing");
    window.WindowState=WindowState.Minimized;if(window.IsVisible||!window.TrayVisible)throw new Exception("Minimize did not retain tray");
    window.RestoreWindow();if(!window.IsVisible||window.WindowState!=WindowState.Normal)throw new Exception("Tray restore failed");
    window.WindowState=WindowState.Maximized;window.WindowState=WindowState.Minimized;window.RestoreWindow();if(window.WindowState!=WindowState.Maximized)throw new Exception("Maximized state lost");
    window.Close();if(window.IsVisible||!window.TrayVisible)throw new Exception("Close did not retain tray");
    window.RestoreWindow();window.PrepareExit();window.Close();if(window.TrayVisible)throw new Exception("Tray not disposed on exit");
    File.WriteAllText(Path.Combine(Common.Bin,"tray-test-result.txt"),"PASS: embedded icon, tray lifetime, minimize/restore, maximized restore, close to tray, explicit exit cleanup\n");
   }catch(Exception error){File.WriteAllText(Path.Combine(Common.Bin,"tray-test-result.txt"),"FAIL: "+error);Environment.ExitCode=1;window.PrepareExit();window.Close();}
  }));app.Run(window);
 }
 internal static void Run(){
  NetworkDiscovery.Test();
  Layer2Tests.Run();
  EventStream.Test();
  Common.ValidateEndpoint("https://203.0.113.1:24443");bool rejected=false;try{Common.ValidateEndpoint("http://203.0.113.1");}catch{rejected=true;}if(!rejected)throw new Exception("plaintext accepted");
  if(Agent.Private(IPAddress.Parse("8.8.8.8"))||!Agent.Private(IPAddress.Parse("172.18.1.1")))throw new Exception("network classification");
  var value=Common.Map("message","test","enabled",true,"items",new[]{Common.Map("id","one")});var roundtrip=Common.Parse(Common.Json(value));if(!Common.Bool(roundtrip,"enabled")||Common.Items(roundtrip,"items").Count()!=1)throw new Exception("IPC serialization");
  TestRelay();using(var process=Process.Start(new ProcessStartInfo(System.Reflection.Assembly.GetExecutingAssembly().Location,"--job-child"){UseShellExecute=false,CreateNoWindow=true})){using(var job=new ProcessJob()){job.Add(process);}if(!process.WaitForExit(5000))throw new Exception("Child survived job termination");}
  File.WriteAllText(Path.Combine(Common.Bin,"self-test-result.txt"),"PASS: SSE framing/auth/reconnect/revocation and state ordering, physical adapter selection, layer2 recovery, endpoint validation, IPC, TCP half-close, child termination\n");
 }
 static void TestRelay(){
  var front=new System.Net.Sockets.TcpListener(IPAddress.Loopback,0);var back=new System.Net.Sockets.TcpListener(IPAddress.Loopback,0);front.Start();back.Start();
  try{
   var backend=Task.Run(async()=>{using(var peer=await back.AcceptTcpClientAsync()){var stream=peer.GetStream();using(var buffer=new MemoryStream()){await stream.CopyToAsync(buffer);var bytes=buffer.ToArray();await stream.WriteAsync(bytes,0,bytes.Length);}}});
   var forwarding=Task.Run(async()=>{using(var peer=await front.AcceptTcpClientAsync())using(var target=new System.Net.Sockets.TcpClient()){await target.ConnectAsync(IPAddress.Loopback,((IPEndPoint)back.LocalEndpoint).Port);await Forward.Relay(peer,target);}});
   using(var client=new System.Net.Sockets.TcpClient()){client.Connect(IPAddress.Loopback,((IPEndPoint)front.LocalEndpoint).Port);client.ReceiveTimeout=5000;var bytes=new byte[1024*1024];new Random(42).NextBytes(bytes);var stream=client.GetStream();stream.Write(bytes,0,bytes.Length);client.Client.Shutdown(System.Net.Sockets.SocketShutdown.Send);using(var result=new MemoryStream()){stream.CopyTo(result);if(Common.Hash(result.ToArray())!=Common.Hash(bytes))throw new Exception("TCP relay corrupted half-close response");}}
   if(!Task.WaitAll(new[]{backend,forwarding},5000))throw new Exception("TCP relay did not close");
  }finally{front.Stop();back.Stop();}
 }
}
}
