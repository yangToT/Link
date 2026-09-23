using System;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace Link {
internal static class UpdateTests {
 internal static void Run(){
  foreach(var pair in new[]{new[]{"0.2.0-alpha.10","0.2.0-alpha.9"},new[]{"0.2.0","0.2.0-rc.1"},new[]{"0.3.0-alpha.1","0.2.9"},new[]{"1.0.0-beta.2","1.0.0-alpha.99"}})if(Updates.Compare(pair[0],pair[1])<=0||Updates.Compare(pair[1],pair[0])>=0)throw new Exception("Update ordering failed");
  string v="0.2.0-alpha.99",name="Link-client-windows-amd64-v"+v+".zip",url="https://github.com/"+Updates.Repository+"/releases/download/v"+v+"/";
  var data=Common.Map("tag_name","v"+v,"prerelease",true,"assets",new[]{Common.Map("name",name,"size",123,"browser_download_url",url+name),Common.Map("name","SHA256SUMS.txt","browser_download_url",url+"SHA256SUMS.txt")});
  var release=Updates.ParseRelease(data,"0.2.0-alpha.7");if(release==null||Updates.ParseRelease(data,"0.1.0")!=null||Updates.ParseRelease(data,"0.3.0-alpha.1")!=null)throw new Exception("Update channel or downgrade failed");
  data["draft"]=true;if(Updates.ParseRelease(data,"0.2.0-alpha.7")!=null)throw new Exception("Draft accepted");data["draft"]=false;
  Common.Items(data,"assets").First()["browser_download_url"]="https://untrusted.invalid/package.zip";if(Updates.ParseRelease(data,"0.2.0-alpha.7")!=null)throw new Exception("Foreign package accepted");
  string hash=new string('a',64);if(Updates.ExpectedHash(release,hash+"  "+name)!=hash)throw new Exception("Checksum parsing failed");
  Refuse(()=>Updates.ExpectedHash(release,hash+"  other.zip"));release.Digest="sha256:"+new string('b',64);Refuse(()=>Updates.ExpectedHash(release,hash+"  "+name));
  var prefs=Common.Map("skipped",v);if(Updates.Due(prefs,v,DateTime.UtcNow)||!Updates.Due(prefs,"0.3.0",DateTime.UtcNow))throw new Exception("Skip should apply to one version");
  prefs["skipped"]="";prefs["deferUntil"]=DateTime.UtcNow.AddHours(24).ToString("o");if(Updates.Due(prefs,v,DateTime.UtcNow))throw new Exception("Reminder not deferred");
  string root=Path.Combine(Path.GetTempPath(),"Link-Update-Test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
  try{
   string archive=Path.Combine(root,"test.zip");
   using(var zip=ZipFile.Open(archive,ZipArchiveMode.Create)){
    Write(zip,"client-update.json",Common.Json(Common.Map("version",v,"format",1,"minimumUpdater",Common.Version)));
    foreach(string file in Updates.Files)Write(zip,file,"test payload");Write(zip,"third_party/LICENSE-test.txt","license");
   }
   Updates.Extract(archive,Path.Combine(root,"valid"),v);if(!File.Exists(Path.Combine(root,"valid","Link.exe")))throw new Exception("Valid update not extracted");
   using(var zip=ZipFile.Open(archive,ZipArchiveMode.Update))Write(zip,"../escape.exe","no");Refuse(()=>Updates.Extract(archive,Path.Combine(root,"bad"),v));if(File.Exists(Path.Combine(root,"escape.exe")))throw new Exception("Zip escaped staging");
   Refuse(()=>Updates.CheckTargets(root,new[]{"..\\foreign.exe"}));
   Refuse(()=>Updates.JobPath("..\\other"));
  }finally{Directory.Delete(root,true);}
  Console.WriteLine("PASS: update version/channel filtering, trusted asset URLs, checksum mismatch, defer/skip, bounded archive extraction");
 }
 static void Write(ZipArchive zip,string name,string value){using(var output=new StreamWriter(zip.CreateEntry(name).Open()))output.Write(value);}
 static void Refuse(Action action){try{action();}catch(InvalidOperationException){return;}throw new Exception("Unsafe update accepted");}
}
}
