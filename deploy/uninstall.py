#!/usr/bin/env python3
"""Remove only the fixed, owned Linux installation. --plan never mutates the host."""
import argparse
import json
import os
from pathlib import Path
import shutil
import shlex
import subprocess

ROOT = Path('/ROOT/Link')
UNITS = Path('/etc/systemd/system')


def inspect(root=ROOT, units=UNITS):
    if root.is_symlink() or root.resolve() != root.absolute() or not (root / '.link-owned').is_file():
        raise RuntimeError('Installation ownership or canonical path cannot be verified')
    names = {'link-server', 'link-network', 'link-peer'}
    names.update(p.stem for p in (root / 'deploy').glob('link-*.service'))
    owned = []
    for name in sorted(names):
        target = units / (name + '.service')
        source = root / 'deploy' / target.name
        if not target.exists() and not target.is_symlink():
            continue
        if not target.is_symlink() or target.resolve() != source or source.is_symlink():
            raise RuntimeError('Refusing to change unowned unit: ' + name)
        text = source.read_text()
        starts = [line[10:] for line in text.splitlines() if line.startswith('ExecStart=')]
        if len(starts) != 1:
            raise RuntimeError('Unit executable does not belong to Link: ' + name)
        executable = Path(shlex.split(starts[0])[0])
        if not executable.resolve().is_relative_to(root.resolve()):
            raise RuntimeError('Unit executable escapes Link directory: ' + name)
        owned.append(name)
    for path in root.rglob('*'):
        if path.is_symlink():
            raise RuntimeError('Review unexpected installation symlink before uninstall: ' + str(path))
    return owned


def run(*args, check=True):
    return subprocess.run(args, check=check, capture_output=True, text=True)


def remove(root=ROOT, units=UNITS, keep_data=False, execute=run):
    names = inspect(root, units)  # Validate the complete plan before stopping anything.
    for name in names:
        execute('systemctl', 'disable', '--now', name + '.service')
        state = execute('systemctl', 'show', name + '.service', '--property=ActiveState', '--value').stdout.strip()
        if state not in ('inactive', 'failed'):
            raise RuntimeError('Link service is still active; installation retained: ' + name)
    # Foreground NetBird normally destroys its own interface during shutdown.
    adapter = execute('ip', '-j', 'link', 'show', 'dev', 'Link0', check=False)
    if adapter.returncode == 0 and json.loads(adapter.stdout):
        raise RuntimeError('Link0 remains after shutdown; retain recovery data and firewall isolation')
    tables = json.loads(execute('nft', '-j', 'list', 'tables').stdout)
    guard = any(x.get('table', {}).get('family') == 'inet' and x['table'].get('name') == 'link_guard' for x in tables.get('nftables', []))
    if guard:
        if not (root / 'deploy' / 'guard.nft').is_file():
            raise RuntimeError('Cannot prove firewall table ownership; retain installation')
        execute('nft', 'delete', 'table', 'inet', 'link_guard')
    for name in names:
        (units / (name + '.service')).unlink()
    execute('systemctl', 'daemon-reload')
    # No package-manager removal, global DNS/route reset or shared database operations.
    if keep_data:
        for child in root.iterdir():
            if child.name in ('data', 'backups', '.link-owned'):
                continue
            if child.is_dir():
                shutil.rmtree(child)
            else:
                child.unlink()
    else:
        shutil.rmtree(root)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--plan', action='store_true')
    parser.add_argument('--yes', action='store_true', help='execute the uninstall')
    parser.add_argument('--keep-data', action='store_true', help='retain identity, configuration and backups')
    args = parser.parse_args()
    names = inspect()
    print('Installation: /ROOT/Link; services: ' + ', '.join(names))
    print('Retain data and backups.' if args.keep_data else 'Remove Link identity, configuration and backups.')
    if args.plan or not args.yes:
        print('Read-only plan. Run with --yes to uninstall.')
        return
    if os.geteuid() != 0:
        parser.error('Run as root')
    remove(keep_data=args.keep_data)
    print('Link removed. Unrelated services and system networking settings were not reset.')


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        raise SystemExit('Uninstall incomplete: ' + str(error))
