using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Link {
internal sealed partial class MainWindow {
 sealed class FeedbackTestButton : Button {internal void Press(bool value){IsPressed=value;}}
 internal static void TestInteractions(){
  var app=new Application();var window=new MainWindow(true);
  window.Loaded+=async(sender,args)=>{
   try{
    var button=new FeedbackTestButton{Content="保存设置"};window.StyleButton(button);window.body.Children.Add(button);button.ApplyTemplate();
    Color normal=((SolidColorBrush)button.Background).Color;button.Press(true);
    if(((SolidColorBrush)button.Background).Color==normal)throw new Exception("Pressed style is overridden by a local value");
    button.Press(false);if(((SolidColorBrush)button.Background).Color!=normal)throw new Exception("Pressed style did not reset");
    button.IsEnabled=false;if(button.Opacity>=1)throw new Exception("Disabled style missing");button.IsEnabled=true;
    var finish=new TaskCompletionSource<bool>();int calls=0;
    var pending=window.RunAction(button,async()=>{calls++;await finish.Task;});
    if(!window.actionPending||window.actionProgress.Visibility!=Visibility.Visible||button.IsEnabled||window.body.IsEnabled||window.navigation.IsEnabled||Convert.ToString(button.Content)!="处理中…")throw new Exception("Busy feedback missing");
    await window.RunAction(button,()=>{calls++;return Task.FromResult(true);});
    if(calls!=1)throw new Exception("Duplicate operation executed");
    string qa=Path.Combine(Common.Bin,"interaction-busy.png");window.RenderImage();File.Copy(Path.Combine(Common.Bin,"client-render.png"),qa,true);
    finish.SetResult(true);await pending;
    if(window.actionPending||window.actionProgress.Visibility!=Visibility.Collapsed||!window.body.IsEnabled||Convert.ToString(button.Content)!="保存设置"||window.feedback.Text!="操作已完成")throw new Exception("Success did not restore controls");
    await window.RunAction(button,()=>{throw new InvalidOperationException("测试失败提示");});
    if(!window.feedback.Text.StartsWith("未完成")||window.actionPending||!window.body.IsEnabled)throw new Exception("Failure feedback or recovery missing");
    window.RenderImage();File.Copy(Path.Combine(Common.Bin,"client-render.png"),Path.Combine(Common.Bin,"interaction-failed.png"),true);
    File.WriteAllText(Path.Combine(Common.Bin,"interaction-test-result.txt"),"PASS: pressed style, reset, disabled state, pending feedback, duplicate suppression, success/failure and restored controls\n");
   }catch(Exception error){File.WriteAllText(Path.Combine(Common.Bin,"interaction-test-result.txt"),"FAIL: "+error);Environment.ExitCode=1;}
   finally{window.PrepareExit();window.Close();}
  };
  app.Run(window);
 }
}
}
