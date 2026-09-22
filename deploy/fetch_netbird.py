"""Download the pinned official server image's executable without a Docker daemon.

Only the selected binary is extracted. No image entrypoints or installation scripts run.
"""
import hashlib, json, pathlib, shutil, tarfile, urllib.request, sys
VERSION = '0.79.0'
REPO = 'netbirdio/netbird-server'
root = pathlib.Path(sys.argv[1]).resolve()
root.mkdir(parents=True, exist_ok=True)
def get(url, headers=None):
    return urllib.request.urlopen(urllib.request.Request(url, headers=headers or {}), timeout=90)
with get('https://auth.docker.io/token?service=registry.docker.io&scope=repository:'+REPO+':pull') as r:
    token=json.load(r)['token']
headers={'Authorization':'Bearer '+token,'Accept':'application/vnd.oci.image.index.v1+json, application/vnd.docker.distribution.manifest.list.v2+json, application/vnd.oci.image.manifest.v1+json, application/vnd.docker.distribution.manifest.v2+json'}
url='https://registry-1.docker.io/v2/'+REPO
with get(url+'/manifests/'+VERSION,headers) as r:
    data=r.read(); manifest=json.loads(data)
if 'manifests' in manifest:
    target=next(m for m in manifest['manifests'] if m['platform']['os']=='linux' and m['platform']['architecture']=='amd64')
    with get(url+'/manifests/'+target['digest'],headers) as r:data=r.read();manifest=json.loads(data)
manifest_digest=hashlib.sha256(data).hexdigest()
found=False
for layer in reversed(manifest['layers']):
    archive=root/'layer.tar.gz'
    hasher=hashlib.sha256()
    with get(url+'/blobs/'+layer['digest'],headers) as response, archive.open('wb') as out:
        while True:
            chunk=response.read(1024*1024)
            if not chunk:break
            hasher.update(chunk);out.write(chunk)
    if 'sha256:'+hasher.hexdigest()!=layer['digest']:raise RuntimeError('Image layer digest mismatch')
    with tarfile.open(archive,'r:*') as tar:
        for member in tar:
            if member.name.lstrip('./')=='go/bin/netbird-server' and member.isfile():
                with tar.extractfile(member) as inp,(root/'netbird-server').open('wb') as out:shutil.copyfileobj(inp,out)
                (root/'netbird-server').chmod(0o755);found=True;break
    archive.unlink()
    if found:break
if not found:raise RuntimeError('Server binary not present in official image')
(root/'upstream.json').write_text(json.dumps({'repository':REPO,'version':VERSION,'manifestSha256':manifest_digest,'binarySha256':hashlib.sha256((root/'netbird-server').read_bytes()).hexdigest()},indent=2))
print('Pinned network server binary downloaded and layer digest verified.')
