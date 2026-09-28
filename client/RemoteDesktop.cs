using System.Collections.Generic;
using System.Net;

namespace Link {
internal static class RemoteDesktop {
 internal static string Address(Dictionary<string,object> snapshot,Dictionary<string,object> device){
  var state=Common.Obj(snapshot,"state");
  if(!Common.Bool(Common.Obj(state,"device"),"connected")||!Common.Bool(device,"connected"))return "";
  var target=Common.Obj(device,"layer2");
  // Local status is fresher than the heartbeat copy in state.device.layer2.
  if(Common.Text(state,"networkMode")=="bridged"&&Common.Bool(snapshot,"layer2Enabled")&&Ready(Common.Obj(snapshot,"layer2"))&&Common.Bool(target,"enabled")&&Ready(target))return Common.Text(target,"ip");
  IPAddress ip;string address=Common.Text(device,"ip");
  return IPAddress.TryParse(address,out ip)&&ip.AddressFamily==System.Net.Sockets.AddressFamily.InterNetwork?ip.ToString():"";
 }
 static bool Ready(Dictionary<string,object> layer){
  string state=Common.Text(layer,"state");IPAddress ip;
  return (state=="attached"||state=="entry-ready")&&IPAddress.TryParse(Common.Text(layer,"ip"),out ip)&&Agent.Private(ip);
 }
}
}
