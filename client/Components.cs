using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.ServiceProcess;
using System.Threading;
using Microsoft.Win32;

namespace Link {
internal static class Components {
 internal static readonly string Root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"Link","softether");
 internal static readonly string[] Services={"SEVPNCLIENT","SEVPNBRIDGE"};
 internal static bool Administrator {get{return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);}}
 internal static Dictionary<string,object> Inspect(){
  int owned=0;bool running=true,foreign=false;
  foreach(var name in Services)using(var dev=Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Services\\"+name+"DEV"))if(dev!=null)foreign=true;
  foreach(var name in Services){using(var key=Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Services\\"+name)){
   if(key==null){running=false;continue;}string role=name=="SEVPNCLIENT"?"client":"bridge";
   string expected=Common.Quote(Path.Combine(Root,role,"vpn"+role+".exe"))+" /service";
   if(!String.Equals(Convert.ToString(key.GetValue("ImagePath")),expected,StringComparison.OrdinalIgnoreCase)){foreign=true;continue;}
   owned++;using(var service=new ServiceController(name))if(service.Status!=ServiceControllerStatus.Running)running=false;
  }}
  bool prepared=owned==2&&File.Exists(Path.Combine(Common.Home,"layer2-local.bin"))&&File.Exists(Path.Combine(Root,"vpncmd.exe"));
  return Common.Map("installed",owned>0||Directory.Exists(Root),"prepared",prepared&&!foreign,"running",owned==2&&running,"foreign",foreign);
 }
 internal static void ServicesRunning(bool start){
  var status=Inspect();if(Common.Bool(status,"foreign"))throw new InvalidOperationException("检测到其他软件安装的网络组件，请保留原安装");
  foreach(string name in Services){if(!ServiceController.GetServices().Any(s=>s.ServiceName==name))continue;using(var s=new ServiceController(name)){
   if(start&&s.Status!=ServiceControllerStatus.Running){s.Start();s.WaitForStatus(ServiceControllerStatus.Running,TimeSpan.FromSeconds(30));}
   if(!start&&s.Status!=ServiceControllerStatus.Stopped){s.Stop();s.WaitForStatus(ServiceControllerStatus.Stopped,TimeSpan.FromSeconds(30));}
  }}
 }
 internal static void EnableCheck(){if(!Common.Bool(Inspect(),"prepared"))throw new InvalidOperationException("请先安装或修复局域网接入组件");ServicesRunning(true);try{Layer2.CheckComponents();}catch{ServicesRunning(false);throw;}}
 internal static void Elevate(string arguments){
  try{using(var p=Process.Start(new ProcessStartInfo(System.Reflection.Assembly.GetExecutingAssembly().Location,arguments){UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden})){p.WaitForExit();if(p.ExitCode!=0)throw new InvalidOperationException("操作未完成，详情请查看功能与组件页面");}}
  catch(System.ComponentModel.Win32Exception e){if(e.NativeErrorCode==1223)throw new InvalidOperationException("已取消 Windows 权限确认，未开始安装");throw;}
 }
 internal static void Worker(string action){
  if(!Administrator)throw new InvalidOperationException("安装组件需要 Windows 管理员权限");
  if(!new[]{"install","repair","remove"}.Contains(action))throw new InvalidOperationException("组件操作无效");
  using(var mutex=new Mutex(false,"Global\\Link.ComponentMaintenance")){
   bool acquired=false;try{try{acquired=mutex.WaitOne(0);}catch(AbandonedMutexException){acquired=true;}if(!acquired)throw new InvalidOperationException("已有组件操作正在进行");
    if(Common.Text(Common.Pipe(Common.Map("action","status")),"version")!=Common.Version)throw new InvalidOperationException("请先更新基础组件");
    Common.Pipe(Common.Map("action","component-maintenance","busy",true,"message",action=="remove"?"正在卸载组件…":"正在准备组件…"));
    try{
     string work=Path.Combine(Common.Home,"component-work");Directory.CreateDirectory(work);
     foreach(string script in new[]{"install-layer2.ps1","prepare-layer2.ps1","manage-components.ps1","uninstall.ps1"}){
      using(var source=System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("Link.Script."+script))using(var output=File.Create(Path.Combine(work,script)))source.CopyTo(output);
     }
     Common.Run(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell\\v1.0\\powershell.exe"),"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "+Common.Quote(Path.Combine(work,"manage-components.ps1"))+" -Action "+action,1200000);
     if(action!="remove"){EnableCheck();ServicesRunning(false);}
     Common.Pipe(Common.Map("action","component-maintenance","busy",false,"message",action=="remove"?"组件已卸载，基础连接保持运行":"组件已就绪，可按需启用局域网接入"));
    }catch{try{Common.Pipe(Common.Map("action","component-maintenance","busy",false,"message","组件操作未完成，请重试修复；详细日志保留在本机"));}catch{}throw;}
   }finally{if(acquired)mutex.ReleaseMutex();}
  }
 }
 internal static SecurityIdentifier Owner(){using(var key=Registry.LocalMachine.OpenSubKey("SOFTWARE\\Link")){string sid=key==null?"":Convert.ToString(key.GetValue("OwnerSID"));return sid==""?null:new SecurityIdentifier(sid);}}
 internal static void SetOwner(){using(var key=Registry.LocalMachine.CreateSubKey("SOFTWARE\\Link")){if(key.GetValue("OwnerSID")==null)key.SetValue("OwnerSID",WindowsIdentity.GetCurrent().User.Value);}}
}
}
