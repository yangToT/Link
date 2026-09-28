using System;
using System.IO;
using System.Linq;
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
    window.TestRemoteDesktop();
    window.snapshot=Common.Map("registered",true,"wanted",true);window.backendReady=true;window.BackendUnavailable();
    if(!Common.Bool(window.snapshot,"registered")||window.backendReady||Convert.ToString(window.connection.Content)!="启动 / 重试")throw new Exception("Backend timeout erased the saved registration view");
    window.snapshot=Common.Map("registered",true,"rejoinRequired",true,"server","https://203.0.113.10:24443");window.Render();
    if(((TextBlock)window.body.Children[0]).Text!="重新加入网络"||window.server.Text!="https://203.0.113.10:24443")throw new Exception("Rejected identity has no rejoin entry");
    window.RenderImage();File.Copy(Path.Combine(Common.Bin,"client-render.png"),Path.Combine(Common.Bin,"interaction-rejoin.png"),true);
    window.snapshot=Common.Map();window.activePage="settings";window.Render();
    if(window.desktopStart.Parent!=window.body||window.desktopStart==window.autoStart)throw new Exception("Desktop and background startup controls must be independent");
    window.RenderImage();File.Copy(Path.Combine(Common.Bin,"client-render.png"),Path.Combine(Common.Bin,"interaction-settings.png"),true);
    window.activePage="devices";window.Render();
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
 void TestRemoteDesktop(){
  var local=Common.Map("state","attached","ip","192.168.20.10","message","已取得局域网地址");
  var peer=Common.Map("id","peer","name","测试设备","connected",true,"ip","100.88.0.11","layer2",Common.Map("enabled",true,"state","attached","ip","192.168.20.11"));
  var self=Common.Map("id","self","name","本机","connected",true,"ip","100.88.0.10");
  var state=Common.Map("networkMode","bridged","device",self,"devices",new[]{self,peer});
  snapshot=Common.Map("registered",true,"state",state,"layer2Enabled",true,"layer2",local);
  foreach(string expected in new[]{"192.168.20.11","100.88.0.11",""}){
   if(expected=="100.88.0.11")local["state"]="waiting-address";
   if(expected=="")peer["connected"]=false;
   Render();
   var buttons=body.Children.OfType<Border>().Select(b=>b.Child).OfType<DockPanel>().SelectMany(p=>p.Children.OfType<Button>()).ToArray();
   if(buttons.Length!=1||buttons[0].IsEnabled!=(expected!="")||Convert.ToString(buttons[0].ToolTip)!=(expected==""?"等待设备连接和地址就绪":"远程地址："+expected))throw new Exception("RDP button address or availability mismatch");
   if(expected=="192.168.20.11"){RenderImage();File.Copy(Path.Combine(Common.Bin,"client-render.png"),Path.Combine(Common.Bin,"interaction-remote-desktop.png"),true);}
  }
 }
}
}
