"""Keep the dedicated SoftEther Stable hub on TCP inside the private overlay."""
import copy
import json
from pathlib import Path
import ssl
import urllib.request


def configure_transport(rpc, hub):
    before = rpc('GetHubExtOptions', {'HubName_str': hub})
    options = copy.deepcopy(before)
    matches = [x for x in options['AdminOptionList']
               if x['Name_str'] == 'DisableUdpAcceleration']
    if len(matches) != 1:
        raise RuntimeError('Expected Stable UDP acceleration option missing')
    if matches[0]['Value_u32'] == 1:
        return
    matches[0]['Value_u32'] = 1
    rpc('SetHubExtOptions', options)
    after = rpc('GetHubExtOptions', {'HubName_str': hub})
    values = lambda x: {v['Name_str']: v['Value_u32'] for v in x['AdminOptionList']}
    if values(after) != values(options):
        rpc('SetHubExtOptions', before)
        raise RuntimeError('Hub transport verification failed; original options restored')


if __name__ == '__main__':
    root = Path('/ROOT/Link')
    if not (root / '.link-owned').is_file():
        raise SystemExit('Dedicated Link installation required')
    cfg = json.loads((root / 'data/config.json').read_text())['layer2']
    # The installed certificate covers loopback; no public management request.
    from urllib.parse import urlparse
    endpoint = urlparse(cfg['apiUrl'])
    if endpoint.scheme != 'https' or endpoint.hostname != '127.0.0.1' or endpoint.path != '/api/':
        raise SystemExit('Loopback HTTPS management endpoint required')
    context = ssl.create_default_context(cadata=cfg['certificate'])
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), urllib.request.HTTPSHandler(context=context))

    def rpc(method, params):
        request = urllib.request.Request(cfg['apiUrl'],
            data=json.dumps({'jsonrpc': '2.0', 'id': 'link-transport', 'method': method, 'params': params}).encode(),
            headers={'Content-Type': 'application/json', 'X-VPNADMIN-PASSWORD': cfg['password']})
        with opener.open(request, timeout=8) as response:
            result = json.load(response)
        if result.get('error') or 'result' not in result:
            raise RuntimeError('Component RPC failed: ' + method)
        return result['result']

    configure_transport(rpc, cfg['hub'])
    print('Dedicated hub: UDP acceleration disabled; other options retained; no service restart.')
