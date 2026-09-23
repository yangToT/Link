using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows.Forms;

internal static class Uninstaller {
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool MoveFileEx(string from,string to,int flags);
 static string Quote(string s){if(s.Contains("\"")||s.Contains("\r")||s.Contains("\n"))throw new ArgumentException("Invalid path");return "\""+s+"\"";}
 [STAThread]static int Main(string[] args){
  try{
   bool worker=args.Length==5&&args[0]=="--worker";
   if(!worker){
    if(args.Length!=0)throw new ArgumentException("Open without arguments; use uninstall.ps1 -Plan for a read-only preview.");
    bool removeIdentity=true;
#if SERVER
    if(MessageBox.Show("卸载 Link 并清除本机身份和配置？\n连接将断开。将移除安装文件及正在使用的解压目录内的 Link 程序文件。\n下载压缩包、其他文件和共享原文件保留。","卸载 Link",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return 0;
#else
    var choice=MessageBox.Show("卸载 Link 时，是否同时清除设备身份和设置？\n\n是：彻底清除，下次安装需要重新加入。\n否：保留身份，下次安装继续使用原设备。\n取消：不卸载。\n\n两种卸载都会断开连接、恢复网络并移除 Link 程序及组件。更新版本无需先卸载。","卸载 Link",MessageBoxButtons.YesNoCancel,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2);
    if(choice==DialogResult.Cancel)return 0;removeIdentity=choice==DialogResult.Yes;
#endif
    string staging=Path.Combine(Path.GetTempPath(),"Link-Uninstall-"+Guid.NewGuid().ToString("N"));
    var acl=new DirectorySecurity();acl.SetAccessRuleProtection(true,false);
    foreach(var sid in new[]{WellKnownSidType.LocalSystemSid,WellKnownSidType.BuiltinAdministratorsSid})acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid,null),FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
    Directory.CreateDirectory(staging,acl);
    string exe=Path.Combine(staging,"Uninstall.exe");File.Copy(Assembly.GetExecutingAssembly().Location,exe);
    Process.Start(new ProcessStartInfo(exe,"--worker "+Quote(AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))+" "+Quote(staging)+" "+Process.GetCurrentProcess().Id+" "+(removeIdentity?"remove":"keep")){UseShellExecute=false,CreateNoWindow=true});return 0;
   }
   string directory=AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
   if(!Path.GetFullPath(args[2]).Equals(directory,StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(directory).StartsWith("Link-Uninstall-",StringComparison.Ordinal))throw new ArgumentException("Invalid uninstall workspace");
   try{using(var parent=Process.GetProcessById(Int32.Parse(args[3])))if(!parent.WaitForExit(10000))throw new IOException("Original uninstaller is still running");}catch(ArgumentException){}
   string script=Path.Combine(directory,"uninstall.ps1"),log=Path.Combine(directory,"result.txt");
   using(var input=Assembly.GetExecutingAssembly().GetManifestResourceStream("Link.Uninstall"))using(var output=File.Create(script))input.CopyTo(output);
   if(args[4]!="remove"&&args[4]!="keep")throw new ArgumentException("Invalid identity option");
   string options=args[4]=="remove"?" -RemoveIdentity":"";
#if SERVER
   options+=" -ProgramRoot "+Quote(args[1]);
#else
   options+=" -SourceRoot "+Quote(args[1]);
#endif
   var command=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell\\v1.0\\powershell.exe"),"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "+Quote(script)+options){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
   Application.EnableVisualStyles();
   bool success=Link.ProgressWindow.Run("卸载 Link",report=>Link.ProgressWindow.Script(command,report,log,"LINK_COMPLETE|uninstall"),"Link 已卸载",log);
   if(!success)return 1;
   File.Delete(script);File.Delete(log);MoveFileEx(Assembly.GetExecutingAssembly().Location,null,4);MoveFileEx(directory,null,4);return 0;
  }catch(Exception error){MessageBox.Show(error.Message,"Link 卸载失败",MessageBoxButtons.OK,MessageBoxIcon.Error);return 1;}
 }
}
