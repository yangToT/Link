using System;
using System.IO;
using Microsoft.Win32;

namespace Link {
internal static class DesktopStartup {
 internal const string RunPath=@"Software\Microsoft\Windows\CurrentVersion\Run",SettingsPath=@"Software\Link\Desktop",ValueName="Link.ClientUI";
 internal static readonly string Executable=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"Link","Link.exe");
 internal static string Command(string executable){return Common.Quote(executable)+" --autostart";}
 internal static bool Enabled {get{using(var run=Registry.CurrentUser.OpenSubKey(RunPath))return run!=null&&Owned(run.GetValue(ValueName),Executable);}}
 internal static bool Owned(object value,string executable){return String.Equals(value as string,Command(executable),StringComparison.OrdinalIgnoreCase);}
 internal static void Save(bool enabled){
  if(enabled)CheckInstalled();
  using(var run=Registry.CurrentUser.CreateSubKey(RunPath))using(var settings=Registry.CurrentUser.CreateSubKey(SettingsPath))Configure(run,settings,Executable,enabled);
 }
 internal static void Initialize(bool enabled){
  CheckInstalled();
  using(var run=Registry.CurrentUser.CreateSubKey(RunPath))using(var settings=Registry.CurrentUser.CreateSubKey(SettingsPath))Migrate(run,settings,Executable,enabled);
 }
 static void CheckInstalled(){
  using(var service=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\LinkAgent"))
   if(!File.Exists(Executable)||service==null||!String.Equals(service.GetValue("ImagePath") as string,Common.Quote(Executable)+" --service",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("请先安装 Link 基础组件，再设置登录启动");
 }
 internal static void Migrate(RegistryKey run,RegistryKey settings,string executable,bool enabled){
  // Migrate the old background-only preference once. Respect later user changes,
  // including deleting the Run entry outside Link.
  if(settings.GetValue("StartupInitialized")==null)Configure(run,settings,executable,enabled);
 }
 internal static void Configure(RegistryKey run,RegistryKey settings,string executable,bool enabled){
  object current=run.GetValue(ValueName);
  if(current!=null&&!Owned(current,executable))throw new InvalidOperationException("Link 登录启动项指向其他程序，请先检查 Windows 启动应用设置");
  if(enabled)run.SetValue(ValueName,Command(executable),RegistryValueKind.String);else run.DeleteValue(ValueName,false);
  settings.SetValue("StartupInitialized",executable,RegistryValueKind.String);
 }
}
}
