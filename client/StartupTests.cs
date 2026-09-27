using System;
using Microsoft.Win32;

namespace Link {
internal static class StartupTests {
 internal static void Run(){
  string root=@"Software\Link.StartupTests\"+Guid.NewGuid().ToString("N"),exe=@"C:\Program Files\Link\Link.exe";
  try{using(var run=Registry.CurrentUser.CreateSubKey(root+@"\Run"))using(var settings=Registry.CurrentUser.CreateSubKey(root+@"\Settings")){
   run.SetValue("OtherApp","keep");DesktopStartup.Migrate(run,settings,exe,true);
   if(Convert.ToString(run.GetValue(DesktopStartup.ValueName))!="\"C:\\Program Files\\Link\\Link.exe\" --autostart")throw new Exception("Login startup command must quote installed path");
   DesktopStartup.Configure(run,settings,exe,false);DesktopStartup.Migrate(run,settings,exe,true);
   if(run.GetValue(DesktopStartup.ValueName)!=null)throw new Exception("Migration overrode disabled desktop startup");
   DesktopStartup.Configure(run,settings,exe,true);run.DeleteValue(DesktopStartup.ValueName);DesktopStartup.Migrate(run,settings,exe,true);
   if(run.GetValue(DesktopStartup.ValueName)!=null)throw new Exception("Migration recreated externally removed startup");
   run.SetValue(DesktopStartup.ValueName,"foreign.exe");
   foreach(bool enabled in new[]{false,true}){bool rejected=false;try{DesktopStartup.Configure(run,settings,exe,enabled);}catch(InvalidOperationException){rejected=true;}if(!rejected||Convert.ToString(run.GetValue(DesktopStartup.ValueName))!="foreign.exe")throw new Exception("Foreign startup entry modified");}
   run.DeleteValue(DesktopStartup.ValueName);settings.DeleteValue("StartupInitialized");DesktopStartup.Migrate(run,settings,exe,false);
   if(run.GetValue(DesktopStartup.ValueName)!=null||settings.GetValue("StartupInitialized")==null||Convert.ToString(run.GetValue("OtherApp"))!="keep")throw new Exception("Disabled migration or unrelated entry changed");
  }}finally{Registry.CurrentUser.DeleteSubKeyTree(root,false);}
  Console.WriteLine("PASS: quoted login startup, migration, disable persistence, external removal, foreign entry protection");
 }
}
}
