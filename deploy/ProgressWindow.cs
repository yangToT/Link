using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Link {
// Shared by the elevated component worker and the standalone uninstaller.
internal sealed class ProgressWindow : Form {
 readonly Label stage=new Label(),detail=new Label();
 readonly ProgressBar progress=new ProgressBar();
 readonly Button done=new Button();
 internal bool Finished {get;private set;}
 internal Exception Failure {get;private set;}
 internal string Stage {get{return stage.Text;}}
 internal ProgressWindow(string title){
  Text=title;ClientSize=new Size(560,295);AutoScaleMode=AutoScaleMode.Dpi;Font=new Font("Microsoft YaHei UI",10);
  FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;StartPosition=FormStartPosition.CenterScreen;
  BackColor=Color.FromArgb(240,245,244);Icon=Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetExecutingAssembly().Location);
  stage.SetBounds(24,25,510,42);stage.Text="正在准备…";
  progress.SetBounds(24,80,510,18);progress.Style=ProgressBarStyle.Marquee;progress.MarqueeAnimationSpeed=25;
  detail.SetBounds(24,116,510,110);detail.Text="操作进行中，请勿关闭电脑。耗时取决于下载速度和系统响应。";
  done.SetBounds(444,243,90,30);done.Text="关闭";done.Enabled=false;done.Click+=(s,e)=>Close();
  Controls.AddRange(new Control[]{stage,progress,detail,done});
  FormClosing+=(s,e)=>{if(!Finished)e.Cancel=true;};
 }
 internal static bool Run(string title,Action<Action<string>> work,string success,string log,bool closeOnSuccess=false){
  using(var window=new ProgressWindow(title)){
   window.Shown+=async(s,e)=>{await window.Execute(work,success,log);if(closeOnSuccess&&window.Failure==null)window.Close();};
   window.ShowDialog();return window.Failure==null;
  }
 }
 internal async Task Execute(Action<Action<string>> work,string success,string log){
  try{await Task.Run(()=>work(text=>BeginInvoke((Action)(()=>stage.Text=text))));stage.Text=success;progress.Style=ProgressBarStyle.Continuous;progress.Value=100;detail.Text="操作已完成。";}
  catch(Exception error){Failure=error;stage.Text="操作未完成";progress.MarqueeAnimationSpeed=0;detail.Text=error.Message+(string.IsNullOrEmpty(log)?"":"\n详细记录："+log);}
  finally{Finished=true;done.Enabled=true;AcceptButton=done;Activate();}
 }
 internal static void Script(ProcessStartInfo command,Action<string> report,string log,string completion){
  command.UseShellExecute=false;command.CreateNoWindow=true;command.RedirectStandardOutput=true;command.RedirectStandardError=true;
  command.StandardOutputEncoding=Encoding.UTF8;command.StandardErrorEncoding=Encoding.UTF8;
  bool complete=false;
  using(var writer=new StreamWriter(log,false,new UTF8Encoding(false))){writer.AutoFlush=true;
   using(var process=new Process{StartInfo=command}){
    process.OutputDataReceived+=(s,e)=>{if(e.Data==null)return;lock(writer){writer.WriteLine(e.Data);if(e.Data==completion)complete=true;}
     if(e.Data.StartsWith("LINK_STAGE|",StringComparison.Ordinal))report(e.Data.Substring(11));};
    process.ErrorDataReceived+=(s,e)=>{if(e.Data!=null)lock(writer)writer.WriteLine(e.Data);};
    process.Start();process.BeginOutputReadLine();process.BeginErrorReadLine();
    process.WaitForExit(); // Keep recovery work alive; never kill a network cleanup on a UI timer.
    if(process.ExitCode!=0)throw new InvalidOperationException("系统操作失败（退出码 "+process.ExitCode+"），请查看日志后重试。");
    if(!complete)throw new InvalidOperationException("未收到完成校验，不能确认操作成功。");
   }
  }
 }
}
}
