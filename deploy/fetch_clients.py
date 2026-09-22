"""Fetch pinned, unmodified official client archives and verify release checksums."""
import hashlib,json,pathlib,sys,tarfile,urllib.request,shutil
ROOT=pathlib.Path(sys.argv[1]).resolve();ROOT.mkdir(parents=True,exist_ok=True)
BASE='https://github.com/netbirdio/netbird/releases/download/v0.79.0/'
def download(url,path):
 with urllib.request.urlopen(url,timeout=120) as source,path.open('wb') as out:shutil.copyfileobj(source,out)
checks=ROOT/'checksums.txt';download(BASE+'netbird_0.79.0_checksums.txt',checks)
sums={line.split()[-1].lstrip('*'):line.split()[0] for line in checks.read_text().splitlines() if len(line.split())==2}
for platform in ['windows','linux']:
 name='netbird_0.79.0_'+platform+'_amd64.tar.gz';archive=ROOT/name
 download(BASE+name,archive)
 if hashlib.sha256(archive.read_bytes()).hexdigest()!=sums[name]:raise RuntimeError('Upstream checksum mismatch')
 target=ROOT/platform;target.mkdir(exist_ok=True)
 with tarfile.open(archive) as tar:
  for member in tar:
   name=pathlib.PurePosixPath(member.name).name
   if member.isfile() and (name in ['netbird','netbird.exe','wintun.dll','LICENSE'] or name.endswith('.dll')):
    with tar.extractfile(member) as inp,(target/name).open('wb') as out:shutil.copyfileobj(inp,out)
 print(platform+' official client verified')
