using System;
using System.Collections.Generic;
using System.Linq;

namespace Link {
internal static class Layer2Availability {
 internal static bool Transient(Dictionary<string,object> snapshot){
  string id=Common.Text(snapshot,"entryId");
  var entry=Common.Items(snapshot,"devices").FirstOrDefault(d=>Common.Text(d,"id")==id);
  return entry!=null&&Common.Text(entry,"state")=="active"&&Common.Bool(Common.Obj(entry,"layer2"),"enabled")&&Common.Bool(Common.Obj(entry,"layer2"),"prepared")&&Common.Bool(Common.Obj(entry,"network"),"bridgeEligible")&&Common.Text(Common.Obj(snapshot,"device"),"state")=="active"&&(!Common.Bool(entry,"connected")||!Common.Bool(Common.Obj(snapshot,"device"),"connected"));
 }
 // The authenticated heartbeat and SSE share this state. Never poll for a plan
 // while it already says the entry is absent, offline or unable to bridge.
 internal static Dictionary<string,object> Fetch(Dictionary<string,object> snapshot,Func<Dictionary<string,object>> request,out string waiting){
  string id=Common.Text(snapshot,"entryId");
  var entry=Common.Items(snapshot,"devices").FirstOrDefault(d=>Common.Text(d,"id")==id);
  waiting=entry==null?"等待管理员指定入口设备":
   !Common.Bool(entry,"connected")||Common.Text(entry,"state")!="active"?"入口设备离线，等待上线通知":
   !Common.Bool(Common.Obj(entry,"layer2"),"enabled")||!Common.Bool(Common.Obj(entry,"layer2"),"prepared")||!Common.Bool(Common.Obj(entry,"network"),"bridgeEligible")?"入口设备尚未就绪，等待状态更新":
   !Common.Bool(Common.Obj(snapshot,"device"),"connected")?"等待本机专用网络连接":"";
  return waiting==""?request():null;
 }
 internal static void Test(){
  var entry=Common.Map("id","entry","state","active","connected",false,"layer2",Common.Map("enabled",true,"prepared",true),"network",Common.Map("bridgeEligible",true));
  var state=Common.Map("entryId","entry","device",Common.Map("connected",true,"state","active"),"devices",new object[]{entry});
  int calls=0;string waiting;Func<Dictionary<string,object>> request=()=>{calls++;return Common.Map("enabled",true);};
  for(int i=0;i<100;i++)if(Fetch(state,request,out waiting)!=null||waiting=="")throw new Exception("Offline entry fetched a plan");
  if(!Transient(state))throw new Exception("Offline entry should retain a short transport grace");
  entry["connected"]=true;if(Fetch(state,request,out waiting)==null||calls!=1)throw new Exception("Online push did not resume plan acquisition");
  // Online leases must still be renewed; suppressing them would expire access.
  Fetch(state,request,out waiting);if(calls!=2)throw new Exception("Online lease renewal suppressed");
  entry["connected"]=false;Fetch(state,request,out waiting);
  entry["connected"]=true;entry["state"]="disabled";Fetch(state,request,out waiting);
  if(Transient(state))throw new Exception("Disabled entry must be cleaned up immediately");
  entry["state"]="active";Common.Obj(entry,"layer2")["enabled"]=false;Fetch(state,request,out waiting);
  Common.Obj(entry,"layer2")["enabled"]=true;Common.Obj(entry,"layer2")["prepared"]=false;Fetch(state,request,out waiting);
  Common.Obj(entry,"layer2")["prepared"]=true;Common.Obj(entry,"network")["bridgeEligible"]=false;Fetch(state,request,out waiting);
  Common.Obj(entry,"network")["bridgeEligible"]=true;Common.Obj(state,"device")["connected"]=false;Fetch(state,request,out waiting);
  state["entryId"]="removed";Fetch(state,request,out waiting);
  if(calls!=2)throw new Exception("Unavailable entry or local network made extra requests");
  Console.WriteLine("PASS: offline entry makes zero plan requests; online push resumes requests and lease renewal; unavailable states stop requests");
 }
}
}
