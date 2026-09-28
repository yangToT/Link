using System;
using System.Collections.Generic;

namespace Link {
internal static class RemoteDesktopTests {
 internal static void Run(){
  var local=Common.Map("state","attached","ip","192.168.20.10");
  var lan=Common.Map("enabled",true,"state","attached","ip","192.168.20.11");
  var device=Common.Map("connected",true,"ip","100.88.0.11","lanIp","10.1.0.11","layer2",lan);
  var self=Common.Map("connected",true,"layer2",Common.Map("state","waiting-address"));
  var state=Common.Map("networkMode","routed","device",self);
  var snapshot=Common.Map("state",state,"layer2Enabled",true,"layer2",local);
  Expect(snapshot,device,"100.88.0.11");
  state["networkMode"]="bridged";
  foreach(string source in new[]{"attached","entry-ready"})foreach(string target in new[]{"attached","entry-ready"}){
   local["state"]=source;lan["state"]=target;Expect(snapshot,device,"192.168.20.11");
  }
  local["state"]="attached";lan["state"]="attached";
  foreach(string pending in new[]{"off","waiting-address","waiting-network","blocked","cleanup-failed","error",""}){
   local["state"]=pending;Expect(snapshot,device,"100.88.0.11");local["state"]="attached";
   lan["state"]=pending;Expect(snapshot,device,"100.88.0.11");lan["state"]="attached";
  }
  foreach(string invalid in new[]{"","invalid","192.168.20.11 /admin","169.254.1.2","127.0.0.1","0.0.0.0","8.8.8.8","::1"}){
   local["ip"]=invalid;Expect(snapshot,device,"100.88.0.11");local["ip"]="192.168.20.10";
   lan["ip"]=invalid;Expect(snapshot,device,"100.88.0.11");lan["ip"]="192.168.20.11";
  }
  snapshot["layer2Enabled"]=false;Expect(snapshot,device,"100.88.0.11");snapshot["layer2Enabled"]=true;
  lan["enabled"]=false;Expect(snapshot,device,"100.88.0.11");lan["enabled"]=true;
  snapshot.Remove("layer2");Expect(snapshot,device,"100.88.0.11");snapshot["layer2"]=local;
  device.Remove("layer2");Expect(snapshot,device,"100.88.0.11");device["layer2"]=lan;
  self["connected"]=false;Expect(snapshot,device,"");self["connected"]=true;
  device["connected"]=false;Expect(snapshot,device,"");device["connected"]=true;
  device["ip"]="";Expect(snapshot,device,"192.168.20.11");
  local["state"]="waiting-address";
  foreach(string invalid in new[]{"","invalid","100.88.0.11 /admin"}){device["ip"]=invalid;Expect(snapshot,device,"");}
  Expect(Common.Map(),Common.Map(),"");
  Console.WriteLine("PASS: RDP uses LAN for attached members and entries; Link fallback for pending/disabled/stale/invalid LAN; offline and missing addresses disabled");
 }
 static void Expect(Dictionary<string,object> snapshot,Dictionary<string,object> device,string address){
  string actual=RemoteDesktop.Address(snapshot,device);if(actual!=address)throw new Exception("RDP address: expected '"+address+"', got '"+actual+"'");
 }
}
}
