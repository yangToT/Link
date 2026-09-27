using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Link {
internal static class InstalledClient {
 internal static bool Redirect(string[] args){
  if(args.Length!=0&&(args.Length!=1||args[0]!="--autostart"))return false;
  try{
   string installed=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"Link","Link.exe");
   using(var registration=Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Link.Client"))
   using(var service=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\LinkAgent")){
    if(registration==null||service==null)return false;
    if(!ShouldRedirect(System.Reflection.Assembly.GetExecutingAssembly().Location,Common.Version,installed,Convert.ToString(registration.GetValue("InstallLocation")),Convert.ToString(registration.GetValue("DisplayVersion")),Convert.ToString(service.GetValue("ImagePath"))))return false;
    Updates.CheckTargets(Path.GetDirectoryName(installed),new[]{"Link.exe"});
    using(var process=Process.Start(new ProcessStartInfo(installed,args.Length==0?"":args[0]){UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(installed)})){}
    return true;
   }
  }catch(IOException){return false;}catch(UnauthorizedAccessException){return false;}catch(System.ComponentModel.Win32Exception){return false;}catch(InvalidOperationException){return false;}
 }
 internal static bool ShouldRedirect(string current,string version,string installed,string location,string installedVersion,string serviceCommand){
  if(String.Equals(Path.GetFullPath(current),Path.GetFullPath(installed),StringComparison.OrdinalIgnoreCase)||!File.Exists(installed))return false;
  if(!String.Equals(location,Path.GetDirectoryName(installed),StringComparison.OrdinalIgnoreCase)||!String.Equals(serviceCommand,Common.Quote(installed)+" --service",StringComparison.OrdinalIgnoreCase))return false;
  return Updates.Compare(installedVersion,version)>=0;
 }
 internal static void Test(){
  string root=Path.Combine(Path.GetTempPath(),"Link-Launch-Test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
  try{
   string installed=Path.Combine(root,"Link.exe"),source=Path.Combine(root,"old-package","Link.exe"),command=Common.Quote(installed)+" --service";File.WriteAllText(installed,"fixture");
   if(!ShouldRedirect(source,"0.2.0-alpha.8",installed,root,"0.2.0-alpha.13",command))throw new Exception("Old package did not select installed update");
   if(ShouldRedirect(installed,"0.2.0-alpha.8",installed,root,"0.2.0-alpha.13",command)||!ShouldRedirect(source,"0.2.0-alpha.13",installed,root,"0.2.0-alpha.13",command)||ShouldRedirect(source,"0.2.0-alpha.14",installed,root,"0.2.0-alpha.13",command))throw new Exception("Launch loop or downgrade selected");
   if(ShouldRedirect(source,"0.2.0-alpha.8",installed,root+"-foreign","0.2.0-alpha.13",command)||ShouldRedirect(source,"0.2.0-alpha.8",installed,root,"0.2.0-alpha.13","foreign.exe --service"))throw new Exception("Foreign installation selected");
   File.Delete(installed);if(ShouldRedirect(source,"0.2.0-alpha.8",installed,root,"0.2.0-alpha.13",command))throw new Exception("Missing installation selected");
  }finally{Directory.Delete(root,true);}
  Console.WriteLine("PASS: installed update handoff, loop/downgrade protection, ownership and missing-file fallback");
 }
}
}
