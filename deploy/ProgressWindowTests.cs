using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Link;
class ProgressWindowTests {
 [STAThread]static void Main(){
  string dir=Path.Combine(Path.GetTempPath(),"Link-Progress-Test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
  try{
   string script=Path.Combine(dir,"probe.ps1"),log=Path.Combine(dir,"result.log");
   var command=new ProcessStartInfo("powershell.exe","-NoProfile -File \""+script+"\"");
   var stages=new List<string>();
   File.WriteAllText(script,"Write-Output 'LINK_STAGE|fixture'; Write-Output 'LINK_COMPLETE|test'; exit 1");
   Reject(()=>ProgressWindow.Script(command,stages.Add,log,"LINK_COMPLETE|test"));
   File.WriteAllText(script,"Write-Output 'SUCCESS'; exit 0");Reject(()=>ProgressWindow.Script(command,stages.Add,log,"LINK_COMPLETE|test"));
   File.WriteAllText(script,"Write-Output 'LINK_STAGE|fixture'; Write-Output 'LINK_COMPLETE|test'");ProgressWindow.Script(command,stages.Add,log,"LINK_COMPLETE|test");
   if(stages.Count!=2)throw new Exception("Stages not streamed");
   Application.EnableVisualStyles();
   foreach(bool fail in new[]{false,true})using(var window=new ProgressWindow("Link progress fixture")){
    window.Shown+=async(s,e)=>{
     var pending=window.Execute(report=>{report("Fixture stage");Thread.Sleep(400);if(fail)throw new IOException("Synthetic failure");},"Complete",log);
     await Task.Delay(150);
     if(!fail){string qa=Path.Combine(Environment.CurrentDirectory,"artifacts","qa");Directory.CreateDirectory(qa);using(var image=new System.Drawing.Bitmap(window.Width,window.Height)){window.DrawToBitmap(image,new System.Drawing.Rectangle(0,0,image.Width,image.Height));image.Save(Path.Combine(qa,"operation-progress.png"));}}
if(window.Stage!="Fixture stage")throw new Exception("UI did not update while work ran");
     window.Close();if(!window.Visible)throw new Exception("Running operation was closed");
     await pending;if(!window.Finished||(window.Failure!=null)!=fail)throw new Exception("Wrong result state");window.Close();
    };window.ShowDialog();
   }
   Console.WriteLine("PASS: streamed stages; nonzero/missing completion rejected; responsive progress window; close blocked during work; failure remains visible");
  }finally{Directory.Delete(dir,true);}
 }
 static void Reject(Action action){try{action();}catch(InvalidOperationException){return;}throw new Exception("False success");}
}
