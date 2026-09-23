"""Create release archives from an explicit allowlist; never package an instance directory."""
import argparse,hashlib,json,pathlib,shutil,tarfile,zipfile
parser=argparse.ArgumentParser()
parser.add_argument('--version',default='0.2.0-alpha.2')
parser.add_argument('--client-only',action='store_true')
args=parser.parse_args()
root=pathlib.Path(__file__).resolve().parents[1];artifacts=root/'artifacts';release=artifacts/'release';release.mkdir(exist_ok=True)
version=args.version
if not version or any(c not in 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789.-' for c in version):parser.error('invalid version')
common=[root/'LICENSE',root/'THIRD-PARTY-NOTICES.md',root/'README.md',*sorted((root/'third_party').glob('*')),*sorted((root/'docs').glob('*.md'))]
def relative(p):return str(p.relative_to(root)).replace('\\','/')
def zip_package(name,files):
 with zipfile.ZipFile(release/name,'w',zipfile.ZIP_DEFLATED,compresslevel=6) as z:
  for source,target in files:z.write(source,target)
base=[(p,relative(p)) for p in common]
zip_package('Link-client-windows-amd64-v'+version+'.zip',base+[(artifacts/'client-windows-amd64'/name,name) for name in ['Link.exe','Uninstall.exe','netbird.exe','wintun.dll']]+[(root/'client/uninstall.ps1','uninstall.ps1')]+[(root/'client'/name,'client/'+name) for name in ['install-layer2.ps1','prepare-layer2.ps1','manage-components.ps1','uninstall.ps1']])
if args.client_only:
 p=release/('Link-client-windows-amd64-v'+version+'.zip')
 (release/'SHA256SUMS.txt').write_text(hashlib.sha256(p.read_bytes()).hexdigest()+'  '+p.name+'\n',encoding='utf8')
 print(p.name+': '+str(p.stat().st_size)+' bytes')
 raise SystemExit(0)
zip_package('Link-server-windows-amd64-v'+version+'.zip',base+[(artifacts/'server-windows-amd64'/name,name) for name in ['LinkServer.exe','Uninstall.exe']]+[(root/'deploy/uninstall-windows-server.ps1','uninstall.ps1')])
files=base+[(artifacts/'server-linux-amd64/link-server','link-server'),(artifacts/'vendor/netbird-server','vendor/netbird-server'),(artifacts/'vendor/linux/netbird','vendor/netbird'),(artifacts/'vendor/upstream.json','vendor/upstream.json'),(artifacts/'vendor/netbird-source-v0.79.0.tar.gz','vendor/netbird-source-v0.79.0.tar.gz'),(root/'deploy/install.sh','deploy/install.sh'),(root/'deploy/bootstrap.py','deploy/bootstrap.py')]
files += [(artifacts/'server-linux-amd64/link-uninstall','link-uninstall'),(root/'deploy/uninstall.py','deploy/uninstall.py'),(root/'deploy/uninstall.sh','deploy/uninstall.sh')]
files += [(root/'deploy/configure-layer2.py','deploy/configure-layer2.py')]
with tarfile.open(release/('Link-server-linux-amd64-v'+version+'.tar.gz'),'w:gz') as archive:
 for source,target in files:
  info=archive.gettarinfo(str(source),arcname=target);info.uid=info.gid=0;info.uname=info.gname='';info.mode=0o755 if target in ['link-server','link-uninstall','vendor/netbird','vendor/netbird-server','deploy/install.sh','deploy/uninstall.sh','deploy/uninstall.py'] else 0o644
  with source.open('rb') as stream:archive.addfile(info,stream)
paths=sorted(p for p in release.iterdir() if ('-v'+version+'.') in p.name and (p.suffix=='.zip' or p.name.endswith('.tar.gz')))
(release/'SHA256SUMS.txt').write_text(''.join(hashlib.sha256(p.read_bytes()).hexdigest()+'  '+p.name+'\n' for p in paths),encoding='utf8')
for p in paths:print(p.name+': '+str(p.stat().st_size)+' bytes')
