#!/usr/bin/env python3
"""Prepare a dedicated SoftEther server without switching Link's connection mode.

Requires the audited 4.44/9807 Linux binaries already built under
/ROOT/Link/softether/vpnserver. Never installs system packages or bridges a NIC.
"""
import base64
import json
import os
from pathlib import Path
import secrets
import shutil
import ssl
import struct
import subprocess
import time
import urllib.request
from urllib.parse import urlparse

ROOT = Path('/ROOT/Link')


def softether_password_hash(data):
    # SoftEther's config format uses SHA-0 (not SHA-1). The plaintext password
    # remains random and private; this implements the upstream legacy format.
    rotate = lambda x, n: ((x << n) | (x >> (32 - n))) & 0xffffffff
    size = len(data) * 8
    data += b'\x80'
    data += b'\0' * ((56 - len(data) % 64) % 64) + struct.pack('>Q', size)
    h = [0x67452301, 0xefcdab89, 0x98badcfe, 0x10325476, 0xc3d2e1f0]
    for offset in range(0, len(data), 64):
        words = list(struct.unpack('>16I', data[offset:offset + 64]))
        for i in range(16, 80):
            words.append(words[i-3] ^ words[i-8] ^ words[i-14] ^ words[i-16])
        a, b, c, d, e = h
        for i, word in enumerate(words):
            if i < 20:
                f, k = (b & c) | (~b & d), 0x5a827999
            elif i < 40:
                f, k = b ^ c ^ d, 0x6ed9eba1
            elif i < 60:
                f, k = (b & c) | (b & d) | (c & d), 0x8f1bbcdc
            else:
                f, k = b ^ c ^ d, 0xca62c1d6
            a, b, c, d, e = (rotate(a, 5) + f + e + k + word) & 0xffffffff, a, rotate(b, 30), c, d
        h = [(x + y) & 0xffffffff for x, y in zip(h, [a, b, c, d, e])]
    return struct.pack('>5I', *h)


def run(*args):
    return subprocess.run(args, check=True, capture_output=True, text=True).stdout


def main():
    os.umask(0o077)
    r = ROOT
    component = r / 'softether/vpnserver'
    unit = r / 'deploy/link-layer2.service'
    link = Path('/etc/systemd/system/link-layer2.service')
    config_path = r / 'data/config.json'
    cfg = json.loads(config_path.read_text())
    if os.geteuid() != 0 or r.resolve() != r or not (r / '.link-owned').is_file():
        raise RuntimeError('Run as root on an owned Link installation')
    if component.resolve() != component or not (component.parent / '.link-owned').is_file():
        raise RuntimeError('Dedicated component ownership missing')
    if cfg.get('layer2') or link.exists() or link.is_symlink() or (component / 'vpn_server.config').exists():
        raise RuntimeError('Existing layer2 installation requires explicit maintenance, not initialization')
    if run('ss', '-H', '-ltn', 'sport = :24448').strip():
        raise RuntimeError('Port 24448 is already occupied')
    overlay = urlparse(cfg['privateUrl']).hostname
    import ipaddress
    if ipaddress.ip_address(overlay) not in ipaddress.ip_network('100.64.0.0/10'):
        raise RuntimeError('Management address is not on the dedicated overlay')
    backup = r / 'backups' / ('layer2-prepare-' + str(int(time.time())))
    backup.mkdir(mode=0o700)
    shutil.copy2(config_path, backup / 'config.json')
    guard = r / 'deploy/guard.nft'
    shutil.copy2(guard, backup / 'guard.nft')
    original_guard = run('nft', 'list', 'table', 'inet', 'link_guard')
    (backup / 'active-guard.nft').write_text(original_guard)
    services = run('systemctl', 'list-units', '--type=service', '--state=running', '--no-legend', '--no-pager')
    names = [s.split()[0] for s in services.splitlines() if s.strip() and s.split()[0] != 'link-server.service']
    before = {n: run('systemctl', 'show', n, '--property=MainPID,ActiveState,ActiveEnterTimestampMonotonic') for n in names}
    (backup / 'services.json').write_text(json.dumps(before))
    password = secrets.token_hex(32)
    cert, key = component / 'link-cert.pem', component / 'link-key.pem'
    run('openssl', 'req', '-x509', '-newkey', 'rsa:3072', '-nodes', '-sha256', '-days', '3650', '-subj', '/CN=Link Layer2',
        '-addext', 'subjectAltName=IP:127.0.0.1,IP:' + overlay, '-keyout', str(key), '-out', str(cert))
    cert_der = subprocess.check_output(['openssl', 'x509', '-in', str(cert), '-outform', 'DER'])
    key_der = subprocess.check_output(['openssl', 'rsa', '-in', str(key), '-traditional', '-outform', 'DER'], stderr=subprocess.DEVNULL)
    b64 = lambda b: base64.b64encode(b).decode()
    # Start with a password, one isolated listener and no physical bridge/NAT/DHCP.
    (component / 'vpn_server.config').write_text('''declare root
{
 declare ServerConfiguration
 {
  uint ServerType 0
  bool NoLinuxArpFilter true
  bool NoHighPriorityProcess true
  bool DisableNatTraversal true
  bool DisableSSTPServer true
  bool DisableOpenVPNServer true
  bool UseKeepConnect false
  bool DisableJsonRpcWebApi false
  byte HashedPassword %s
  byte ServerCert %s
  byte ServerKey %s
 }
 declare ListenerList
 {
  declare Listener0
  {
   bool Enabled true
   uint Port 24448
  }
 }
 declare DDnsClient
 {
  bool Disabled true
 }
 declare IPsec
 {
  bool L2TP_IPsec false
  bool L2TP_Raw false
  bool EtherIP_IPsec false
 }
 declare VirtualHUB
 {
 }
}
''' % (b64(softether_password_hash(password.encode())), b64(cert_der), b64(key_der)))
    (component / 'lang.config').write_text('en\n')
    rules = 'insert rule inet link_guard input iifname != "lo" iifname != "Link0" tcp dport 24448 drop\ninsert rule inet link_guard input iifname "Link0" tcp dport 24448 accept\n'
    rule_file = component.parent / 'access.nft'
    rule_file.write_text(rules)
    run('nft', '-c', '-f', str(rule_file))
    started = False
    try:
        run('nft', '-f', str(rule_file))
        # Existing guard is loaded atomically at network startup; retain all old rules.
        active = run('nft', 'list', 'table', 'inet', 'link_guard')
        guard.write_text('delete table inet link_guard\n' + active)
        unit.write_text('[Unit]\nDescription=Link private layer2 exchange\nAfter=network-online.target link-peer.service\nWants=network-online.target\n\n[Service]\nType=forking\nWorkingDirectory=' + str(component) + '\nExecStart=' + str(component / 'vpnserver') + ' start\nExecStop=' + str(component / 'vpnserver') + ' stop\nRestart=on-failure\nRestartSec=5\nTimeoutStopSec=30\nUMask=0077\n\n[Install]\nWantedBy=multi-user.target\n')
        link.symlink_to(unit)
        run('systemctl', 'daemon-reload')
        run('systemctl', 'start', 'link-layer2')
        started = True
        context = ssl.create_default_context(cafile=str(cert))
        def rpc(method, params):
            data = json.dumps({'jsonrpc': '2.0', 'id': 'link-setup', 'method': method, 'params': params}).encode()
            request = urllib.request.Request('https://127.0.0.1:24448/api/', data=data,
                headers={'Content-Type': 'application/json', 'X-VPNADMIN-PASSWORD': password})
            with urllib.request.urlopen(request, context=context, timeout=8) as response:
                result = json.load(response)
            if result.get('error') or 'result' not in result:
                raise RuntimeError('SoftEther RPC failed: ' + method)
            return result['result']
        info = None
        for _ in range(10):
            try:
                info = rpc('GetServerInfo', {})
                break
            except Exception as error:
                last_error = type(error).__name__ + ': ' + str(error)
                time.sleep(1)
        if info is None:
            raise RuntimeError('Authenticated component RPC failed: ' + last_error)
        if info.get('ServerBuildInt_u32') != 9807:
            raise RuntimeError('Component version differs from audited build')
        rpc('CreateHub', {'HubName_str': 'LINK', 'AdminPasswordPlainText_str': secrets.token_hex(32), 'Online_bool': True, 'MaxSession_u32': 128})
        rpc('GetHub', {'HubName_str': 'LINK'})
        from layer2_transport import configure_transport
        configure_transport(rpc, 'LINK')
        rpc('DisableSecureNAT', {'HubName_str': 'LINK'})
        if rpc('GetHubStatus', {'HubName_str': 'LINK'}).get('SecureNATEnabled_bool') is not False:
            raise RuntimeError('Virtual NAT/DHCP unexpectedly enabled')
        # Exercise exactly the user-policy API that Link calls, then remove the fixture.
        fixture = 'link-setup-' + secrets.token_hex(6)
        try:
            rpc('CreateUser', {'HubName_str': 'LINK', 'Name_str': fixture, 'AuthType_u32': 1, 'Auth_Password_str': secrets.token_hex(32), 'UsePolicy_bool': True, 'policy:Access_bool': False, 'policy:Ver3_bool': True})
            rpc('SetUser', {'HubName_str': 'LINK', 'Name_str': fixture, 'AuthType_u32': 1, 'Auth_Password_str': secrets.token_hex(32), 'UsePolicy_bool': True, 'policy:Access_bool': False, 'policy:Ver3_bool': True})
            policy = rpc('GetUser', {'HubName_str': 'LINK', 'Name_str': fixture})
            if policy.get('policy:Access_bool') is not False:
                raise RuntimeError('Component did not preserve the disabled user policy')
            rpc('EnumSession', {'HubName_str': 'LINK'})
        finally:
            rpc('DeleteUser', {'HubName_str': 'LINK', 'Name_str': fixture})
        cfg['layer2'] = {'endpoint': overlay + ':24448', 'apiUrl': 'https://127.0.0.1:24448/api/', 'hub': 'LINK', 'password': password, 'certificate': cert.read_text()}
        temporary = config_path.with_suffix('.layer2.tmp')
        temporary.write_text(json.dumps(cfg, indent=2))
        temporary.replace(config_path)
        run('systemctl', 'enable', 'link-layer2')
        run('systemctl', 'restart', 'link-server')
        run('systemctl', 'is-active', 'link-server')
        for n, identity in before.items():
            if run('systemctl', 'show', n, '--property=MainPID,ActiveState,ActiveEnterTimestampMonotonic') != identity:
                raise RuntimeError('Unrelated service identity changed: ' + n)
        print('Prepared SoftEther 4.44/9807; authenticated hub/user/session APIs passed.')
        print('TCP 24448 restricted to loopback and Link0; original network mode retained.')
        print('Existing unaffected service identities unchanged:', len(before))
        print('Backup:', backup)
    except Exception:
        if started:
            subprocess.run(['systemctl', 'disable', '--now', 'link-layer2'], capture_output=True)
        if link.is_symlink() and link.resolve() == unit:
            link.unlink()
            run('systemctl', 'daemon-reload')
        if (component / 'vpn_server.config').exists():
            shutil.move(str(component / 'vpn_server.config'), str(backup / 'failed-vpn_server.config'))
        shutil.copy2(backup / 'config.json', config_path)
        # Remove only this setup's rules, atomically restoring the prior owned table.
        recovery = backup / 'restore-guard.nft'
        recovery.write_text('delete table inet link_guard\n' + original_guard)
        run('nft', '-f', str(recovery))
        shutil.copy2(backup / 'guard.nft', guard)
        run('systemctl', 'restart', 'link-server')
        raise


if __name__ == '__main__':
    main()
