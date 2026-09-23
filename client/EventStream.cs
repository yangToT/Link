using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Link {
internal sealed class StateOrder {
 string epoch="";ulong revision;readonly Queue<string> retired=new Queue<string>();
 internal bool Accept(Dictionary<string,object> value){
  string next=Common.Text(value,"streamEpoch");ulong number;if(next=="")return epoch=="";
  if(!UInt64.TryParse(Common.Text(value,"revision"),out number))return false;
  if(next!=epoch){if(retired.Contains(next))return false;if(epoch!=""){retired.Enqueue(epoch);if(retired.Count>8)retired.Dequeue();}epoch=next;revision=number;return true;}
  if(number<revision)return false;revision=number;return true;
 }
}
// Control-state notifications only. File traffic and VPN packets never pass here.
internal sealed class EventStream : IDisposable {
 readonly Dictionary<string,object> config;readonly Action<Dictionary<string,object>> changed;readonly Action revoked;
 readonly object gate=new object();readonly ManualResetEvent stop=new ManualResetEvent(false);HttpWebRequest request;volatile bool closed;
 internal EventStream(Dictionary<string,object> config,Action<Dictionary<string,object>> changed,Action revoked){this.config=new Dictionary<string,object>(config);this.changed=changed;this.revoked=revoked;Task.Run((Action)Listen);}
 void Listen(){int delay=1000;try{while(!closed){
  try{
   var req=Common.NewRequest(Common.Text(config,"server")+"/agent/events",Common.Text(config,"pin"),Common.Text(config,"token"));req.Accept="text/event-stream";req.ReadWriteTimeout=45000;req.ConnectionGroupName="Link.Events";
   lock(gate){if(closed)return;request=req;}
   using(var response=(HttpWebResponse)req.GetResponse()){
    if(!response.ContentType.StartsWith("text/event-stream",StringComparison.OrdinalIgnoreCase))throw new IOException("Invalid event response");
    delay=1000;using(var reader=new StreamReader(response.GetResponseStream(),new UTF8Encoding(false,true)))ReadEvents(reader,(type,data)=>{if(closed)return;if(type=="state")changed(Common.Parse(data));else if(type=="revoked"){revoked();Dispose();}});
   }
  }catch(WebException e){var response=e.Response as HttpWebResponse;int status=response==null?0:(int)response.StatusCode;if(response!=null)response.Dispose();if(!closed&&(status==401||status==403)){revoked();Dispose();return;}if(status==404)delay=60000;}catch{ /* Existing heartbeat remains an independent failure detector. */ }
  finally{lock(gate){request=null;}}
  if(stop.WaitOne(delay))return;delay=Math.Min(delay*2,30000);
 }}finally{stop.Dispose();}}
 internal static void ReadEvents(TextReader reader,Action<string,string> dispatch){
  var line=new StringBuilder();var data=new StringBuilder();string type="message";int c;
  while((c=reader.Read())>=0){if(c!='\n'){line.Append((char)c);if(line.Length>1048576)throw new IOException("Event line too large");continue;}
   string text=line.ToString().TrimEnd('\r');line.Clear();
   if(text==""){if(data.Length>0)dispatch(type,data.ToString().TrimEnd('\n'));data.Clear();type="message";continue;}
   if(text.StartsWith(":"))continue;int colon=text.IndexOf(':');string field=colon<0?text:text.Substring(0,colon),value=colon<0?"":text.Substring(colon+1);if(value.StartsWith(" "))value=value.Substring(1);
   if(field=="event")type=value;if(field=="data"){data.Append(value).Append('\n');if(data.Length>1048576)throw new IOException("Event too large");}
  }
 }
 public void Dispose(){lock(gate){if(closed)return;closed=true;stop.Set();if(request!=null)request.Abort();}}
 internal static void Test(){
  var order=new StateOrder();if(!order.Accept(Common.Map("streamEpoch","a","revision",3))||order.Accept(Common.Map("streamEpoch","a","revision",2))||!order.Accept(Common.Map("streamEpoch","b","revision",0))||order.Accept(Common.Map("streamEpoch","a","revision",4)))throw new Exception("Out-of-order state accepted");
  var events=new List<string>();ReadEvents(new StringReader(": keepalive\r\n\r\nevent: state\ndata: {\"x\":\ndata: 1}\n\nevent: revoked\ndata: {}\n\ndata: partial"),(kind,data)=>events.Add(kind+":"+data));
  if(events.Count!=2||events[0]!="state:{\"x\":\n1}"||events[1]!="revoked:{}")throw new Exception("SSE framing failed");
  bool rejected=false;try{ReadEvents(new StringReader("data: "+new string('x',1048577)),(kind,data)=>{});}catch(IOException){rejected=true;}if(!rejected)throw new Exception("Oversize event accepted");
  NetworkTest();
 }
 static void NetworkTest(){
  var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;
  try{using(var received=new ManualResetEvent(false))using(var revoked=new ManualResetEvent(false)){
   var server=Task.Run(()=>{for(int i=0;i<2;i++)using(var client=listener.AcceptTcpClient())using(var stream=client.GetStream()){
    var input=new StreamReader(stream,Encoding.UTF8);bool authorized=false;string line;while((line=input.ReadLine())!=null&&line!="")if(line=="Authorization: Bearer synthetic-event-test")authorized=true;if(!authorized)throw new Exception("Stream authorization missing");
    var output=new StreamWriter(stream,new UTF8Encoding(false)){AutoFlush=true};output.Write("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nConnection: close\r\n\r\n"+(i==0?"event: state\ndata: {\"action\":\"keep\"}\n\n":"event: revoked\ndata: {}\n\n"));
   }});
   using(var events=new EventStream(Common.Map("server","http://127.0.0.1:"+port,"pin","","token","synthetic-event-test"),value=>{if(Common.Text(value,"action")=="keep")received.Set();},()=>revoked.Set())){
    if(!received.WaitOne(5000)||!revoked.WaitOne(5000)||!server.Wait(2000))throw new Exception("SSE delivery, reconnect or revocation failed");
   }
  }}finally{listener.Stop();}
 }
}
}
