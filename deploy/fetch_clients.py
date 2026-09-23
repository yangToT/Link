"""Fetch verified upstream runtime files. No installers or network configuration run."""
import argparse
import hashlib
import pathlib
import shutil
import tarfile
import urllib.request
import zipfile

VERSION = '0.79.0'
BASE = 'https://github.com/netbirdio/netbird/releases/download/v' + VERSION + '/'
HASHES = {
    'windows': 'e9920ae7107fa874f1b0208b3884367126edd9405ab962a4af995e384c1f6f88',
    'linux': 'd6f8d26fa21527772f87094557e1196f2a669b1ab4033f650424dce33ba71efd',
}
WINTUN_HASH = '07c256185d6ee3652e09fa55c0b673e2624b565e02c4b9091c79ca7d2f24ef51'


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def download(url, path, expected):
    if path.is_file() and digest(path) == expected:
        return
    partial = path.with_name(path.name + '.partial')
    try:
        with urllib.request.urlopen(url, timeout=120) as source, partial.open('wb') as out:
            shutil.copyfileobj(source, out)
        if digest(partial) != expected:
            raise RuntimeError('Upstream checksum mismatch: ' + path.name)
        partial.replace(path)
    finally:
        partial.unlink(missing_ok=True)


def fetch(root, platforms, client_output=None):
    root.mkdir(parents=True, exist_ok=True)
    for platform in platforms:
        name = 'netbird_' + VERSION + '_' + platform + '_amd64.tar.gz'
        archive = root / name
        download(BASE + name, archive, HASHES[platform])
        target = root / platform
        target.mkdir(exist_ok=True)
        binary = 'netbird.exe' if platform == 'windows' else 'netbird'
        # Extract exact members only; never unpack arbitrary archive paths.
        with tarfile.open(archive) as tar:
            for name in [binary, 'LICENSE']:
                member = tar.getmember(name)
                if not member.isfile():
                    raise RuntimeError('Invalid archive member: ' + name)
                with tar.extractfile(member) as source, (target / name).open('wb') as out:
                    shutil.copyfileobj(source, out)
        if platform == 'windows':
            archive = root / 'wintun-0.14.1.zip'
            download('https://www.wintun.net/builds/wintun-0.14.1.zip', archive, WINTUN_HASH)
            with zipfile.ZipFile(archive) as z:
                (target / 'wintun.dll').write_bytes(z.read('wintun/bin/amd64/wintun.dll'))
                (target / 'Wintun-LICENSE.txt').write_bytes(z.read('wintun/LICENSE.txt'))
        print(platform + ' official runtime verified')
    if client_output:
        client_output.mkdir(parents=True, exist_ok=True)
        for name in ['netbird.exe', 'wintun.dll']:
            shutil.copy2(root / 'windows' / name, client_output / name)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('directory', type=pathlib.Path)
    parser.add_argument('--platform', choices=['windows', 'linux', 'all'], default='all')
    parser.add_argument('--client-output', type=pathlib.Path)
    args = parser.parse_args()
    if args.client_output and args.platform == 'linux':
        parser.error('--client-output requires Windows runtime')
    fetch(args.directory.resolve(), ['windows', 'linux'] if args.platform == 'all' else [args.platform], args.client_output)