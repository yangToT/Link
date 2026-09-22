"""Idempotent isolated Linux deployment. Requires root, systemd, nft, Python 3.9+."""
import base64,json,os,pathlib,secrets,subprocess,sys,time,urllib.request,ipaddress
ROOT=pathlib.Path('/ROOT/Link');DATA=ROOT/'data';DEPLOY=ROOT/'deploy'
host=str(ipaddress.ip_address(sys.argv[1]));os.umask(0o077)
def run(*args):subprocess.run(args,check=True,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
def call(method,path,body=None,token=None):
 headers={'Content-Type':'application/json'}
 if token:headers['Authorization']='Token '+token
 request=urllib.request.Request('http://127.0.0.1:24445'+path,data=json.dumps(body).encode() if body is not None else None,headers=headers,method=method)
 with urllib.request.urlopen(request,timeout=20) as response:return json.load(response)
def unit(name,exec_start,extra=''):
 target=pathlib.Path('/etc/systemd/system')/(name+'.service')
 source=DEPLOY/(name+'.service')
 if target.exists() and (not target.is_symlink() or target.resolve()!=source):raise RuntimeError('Refusing to overwrite an unowned systemd unit')
 text='[Unit]\nDescription=Link '+name+'\nAfter=network-online.target\nWants=network-online.target\n\n[Service]\nType=simple\nWorkingDirectory=/ROOT/Link\nUMask=0077\nRestart=on-failure\nRestartSec=5\n'+extra+'ExecStart='+exec_start+'\n\n[Install]\nWantedBy=multi-user.target\n'
 changed=source.exists() and source.read_text()!=text
 source.write_text(text)
 source.chmod(0o644)
 if not target.exists():target.symlink_to(source)
 run('systemctl','daemon-reload');run('systemctl','enable',name);run('systemctl','restart' if changed else 'start',name)
for directory in ['bin','data','data/netbird','deploy','logs','vendor']:(ROOT/directory).mkdir(parents=True,exist_ok=True)
if not (ROOT/'.link-owned').exists():raise RuntimeError('Deployment directory requires ownership marker')
for binary in ['bin/link-server','vendor/netbird-server','vendor/netbird']:(ROOT/binary).chmod(0o755)
cfg_path=DATA/'config.json'
if not cfg_path.exists():run(str(ROOT/'bin/link-server'),'-data',str(DATA),'-command','init','-public-host',host)
cfg=json.loads(cfg_path.read_text())
network_config=DATA/'netbird.yaml'
if not network_config.exists():
 # JSON is a YAML subset; avoid interpolating any secret into shell text.
 config={'server':{'listenAddress':'127.0.0.1:24445','exposedAddress':cfg['publicUrl'],'stunPorts':[3478],'metricsPort':24447,'healthcheckAddress':'127.0.0.1:24446','logLevel':'warn','logFile':'console','authSecret':secrets.token_urlsafe(48),'dataDir':str(DATA/'netbird'),'disableAnonymousMetrics':True,'disableGeoliteUpdate':True,'auth':{'issuer':'http://127.0.0.1:24445/oauth2','localAuthDisabled':False,'signKeyRefreshEnabled':False,'dashboardRedirectURIs':['http://127.0.0.1/nb-auth'],'cliRedirectURIs':['http://localhost:53000/']},'store':{'engine':'sqlite','encryptionKey':base64.b64encode(secrets.token_bytes(32)).decode()}}}
 network_config.write_text(json.dumps(config,indent=2))
# Upstream metrics binds all interfaces. An owned nft table isolates only Link internal ports.
guard=DEPLOY/'guard.nft'
guard.write_text('delete table inet link_guard\ntable inet link_guard {\n chain input { type filter hook input priority -10; policy accept;\n iifname != "lo" tcp dport {24445,24446,24447} drop\n iifname != "lo" iifname != "Link0" tcp dport 24444 drop\n iifname "Link0" ct state established,related accept\n iifname "Link0" tcp dport 24444 accept\n iifname "Link0" drop\n }\n}\n')
guard_script=DEPLOY/'guard.sh'
guard_script.write_text('#!/bin/sh\nset -eu\nnft list table inet link_guard >/dev/null 2>&1 || nft add table inet link_guard\nnft -f /ROOT/Link/deploy/guard.nft\n')
guard_script.chmod(0o700);run(str(guard_script))
unit('link-network','/ROOT/Link/vendor/netbird-server --config /ROOT/Link/data/netbird.yaml','Environment=NB_SETUP_PAT_ENABLED=true\nEnvironment=NB_DISABLE_GEOLOCATION=true\nExecStartPre=/ROOT/Link/deploy/guard.sh\n')
for attempt in range(30):
 try:call('GET','/api/instance');break
 except Exception:time.sleep(1)
if not cfg.get('backendToken'):
 owner={'email':'owner@link.invalid','name':'Link owner','password':secrets.token_urlsafe(32)+'Aa1!','create_pat':True,'pat_expire_in':365}
 response=call('POST','/api/setup',owner)
 token=response.get('personal_access_token')
 if not isinstance(token,str) or not token:raise RuntimeError('Initial setup did not return an API token')
 (DATA/'owner.json').write_text(json.dumps(owner));cfg['backendToken']=token
 groups=call('GET','/api/groups',token=token);cfg['allGroup']=next(g['id'] for g in groups if g['name']=='All')
 cfg_path.write_text(json.dumps(cfg,indent=2))
unit('link-server','/ROOT/Link/bin/link-server -data /ROOT/Link/data')
if not (DATA/'cloud-key').exists() and not (DATA/'cloud-network.json').exists():
 response=call('POST','/api/setup-keys',{'name':'Link management peer','type':'one-off','expires_in':86400,'usage_limit':1,'auto_groups':[]},cfg['backendToken']);(DATA/'cloud-key').write_text(response['key'])
start='/ROOT/Link/vendor/netbird up --foreground-mode --no-browser --disable-dns --disable-ipv6 --disable-client-routes --disable-server-routes --interface-name Link0 --wireguard-port 51821 --config /ROOT/Link/data/cloud-network.json --management-url '+cfg['publicUrl']+' --log-file /ROOT/Link/logs/cloud-network.log --log-level warn'
if (DATA/'cloud-key').exists():start+=' --setup-key-file /ROOT/Link/data/cloud-key'
unit('link-peer',start,'Environment=SSL_CERT_FILE=/ROOT/Link/data/ca.pem\n')
overlay=None
for attempt in range(40):
 peers=call('GET','/api/peers',token=cfg['backendToken'])
 for peer in peers:
  if peer.get('connected') and peer.get('ip'):overlay=peer['ip'];break
 if overlay:break
 time.sleep(1)
if not overlay:raise RuntimeError('Management peer did not connect; inspect Link logs')
if cfg['privateListen']!=overlay+':24444':
 cfg['privateListen']=overlay+':24444';cfg['privateUrl']='https://'+overlay+':24444';cfg_path.write_text(json.dumps(cfg,indent=2))
 run(str(ROOT/'bin/link-server'),'-data',str(DATA),'-command','certificate','-hosts',host+',127.0.0.1,'+overlay)
 run('systemctl','restart','link-server')
print('Link control plane, network backend and private management peer are running.')
