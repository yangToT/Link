using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Link {
internal static class Layer2Tests {
 internal static void Run(){
  string expected="Test Ethernet (ID=2814777680)";
  if(Layer2.BridgeDevice("Test Ethernet","{11111111-1111-1111-1111-111111111111}","Device Name|"+expected+"\r\n")!=expected)throw new Exception("SoftEther adapter ID mismatch");
  bool refused=false;try{Layer2.BridgeDevice("Test Ethernet","{22222222-2222-2222-2222-222222222222}",expected);}catch(InvalidOperationException){refused=true;}if(!refused)throw new Exception("Wrong physical adapter accepted");

  var plan=Common.Map("hub","LINK","username","link-test","password",new string('a',64),"endpoint","100.88.0.1:24448","role","member","networks",new[]{"192.168.20.0/24"});
  var local=Common.Map("network",Common.Map("adapterId","physical","bridgeEligible",false),"networks",new[]{"192.168.20.0/23"});
  Reject(plan,local,"重叠");plan["role"]="entry";Reject(plan,local,"有线");
  plan["endpoint"]="192.0.2.1:24448";Reject(plan,local,"专用网络");
  plan["endpoint"]="100.88.0.1:24448";plan["username"]="name\r\nAccountDelete foreign";Reject(plan,local,"参数无效");
  string directory=Path.Combine(Path.GetTempPath(),"Link-Layer2-Test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
  try{
   string file=Path.Combine(directory,"layer2-journal.json");
   File.WriteAllText(file,Common.Json(Common.Map("account","Link-ABCDEF012345","nic","LNKABCDEF012345","role","member","accountCreated",true,"nicCreated",true,"bridgeCreated",false)));
   var commands=new List<string>();bool fail=true;
   Func<Dictionary<string,object>,bool,string,bool,string> cli=(cfg,entry,text,cleanup)=>{if(!cleanup||entry)throw new Exception("Invalid cleanup context");commands.Add(text);if(fail&&text.StartsWith("NicDelete"))throw new IOException("Synthetic driver busy");return "";};
   Func<Dictionary<string,object>,string> guard=p=>{if(Common.Text(p,"action")!="cleanup")throw new Exception("Unexpected mutation");return "{\"routeOwned\":false}";};
   var layer=new Layer2(directory,()=>Common.Map(),cli,guard);layer.Stop();
   if(!File.Exists(file)||Common.Text(layer.Status,"state")!="cleanup-failed")throw new Exception("Failed cleanup lost recovery journal");
   if(!Common.Text(layer.Status,"message").Contains("虚拟网卡"))throw new Exception("Cleanup failure stage missing");
   if(commands.Any(c=>c.Contains("foreign"))||!commands.Contains("AccountDisconnect Link-ABCDEF012345"))throw new Exception("Ownership or disconnect order failed");
   int deletedAccounts=commands.Count(c=>c.StartsWith("AccountDelete"));fail=false;
   // A fresh process must resume only incomplete operations.
   layer=new Layer2(directory,()=>Common.Map(),cli,guard);layer.Recover();
   if(File.Exists(file)||Common.Text(layer.Status,"state")!="off"||commands.Count(c=>c.StartsWith("AccountDelete"))!=deletedAccounts)throw new Exception("Restart recovery not idempotent");
   int count=commands.Count;layer.Stop();if(commands.Count!=count)throw new Exception("Clean disconnect changed resources");
  }finally{Directory.Delete(directory,true);}
 }
 static void Reject(Dictionary<string,object> plan,Dictionary<string,object> local,string expected){try{Layer2.ValidatePlan(plan,local);}catch(InvalidOperationException e){if(e.Message.Contains(expected))return;throw;}throw new Exception("Unsafe plan accepted");}
}
}
