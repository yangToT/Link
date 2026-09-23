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
    window.availableUpdate=new UpdateRelease{Version="0.2.0-alpha.99"};window.downloadedUpdate="";window.ShowUpdate();
    var choices=(System.Windows.Controls.WrapPanel)window.updateBanner.Children[2];
    if(choices.Children.Count!=4||Convert.ToString(((Button)choices.Children[0]).Content)!="立即更新")throw new Exception("Update choices missing");
    window.downloadedUpdate="test.zip";window.ShowUpdate();choices=(System.Windows.Controls.WrapPanel)window.updateBanner.Children[2];
    if(choices.Children.Count!=3||Convert.ToString(((Button)choices.Children[0]).Content)!="立即重启"||Convert.ToString(((Button)choices.Children[1]).Content)!="稍后重启")throw new Exception("Restart choices missing");
    var updateFinish=new TaskCompletionSource<bool>();int updateCalls=0;var updateButton=window.UpdateButton("更新测试",async()=>{updateCalls++;await updateFinish.Task;});
    updateButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));updateButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
    if(updateCalls!=1||updateButton.IsEnabled)throw new Exception("Update double click not suppressed");updateFinish.SetResult(true);await Task.Delay(30);if(!updateButton.IsEnabled)throw new Exception("Update button did not recover");
    File.WriteAllText(Path.Combine(Common.Bin,"interaction-test-result.txt"),"PASS: pressed style, reset, disabled state, pending feedback, duplicate suppression, success/failure and restored controls\n");
   }catch(Exception error){File.WriteAllText(Path.Combine(Common.Bin,"interaction-test-result.txt"),"FAIL: "+error);Environment.ExitCode=1;}
   finally{window.PrepareExit();window.Close();}
  };
  app.Run(window);
 }
}
}
