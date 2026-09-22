#!/bin/sh
set -eu
test "$(id -u)" = 0 || { echo 'Run as root.'; exit 1; }
test "$#" = 1 || { echo 'Usage: sh deploy/install.sh PUBLIC_IP'; exit 1; }
root=/ROOT/Link
source=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
test ! -e "$root" || test -f "$root/.link-owned" || { echo 'Refusing to overwrite an unowned directory.'; exit 1; }
mkdir -p "$root/bin" "$root/vendor" "$root/deploy" "$root/data" "$root/logs" "$root/backups"
chmod 700 "$root" "$root/data"
touch "$root/.link-owned"
if test -e "$root/bin/link-server"; then cp "$root/bin/link-server" "$root/backups/link-server-$(date +%Y%m%d%H%M%S)"; fi
cp "$source/link-server" "$root/bin/link-server.new"
chmod 755 "$root/bin/link-server.new"
mv "$root/bin/link-server.new" "$root/bin/link-server"
for binary in netbird netbird-server; do
 test -f "$source/vendor/$binary" || { echo 'Package is missing network components.'; exit 1; }
 cp "$source/vendor/$binary" "$root/vendor/$binary.new"
 chmod 755 "$root/vendor/$binary.new"
 mv "$root/vendor/$binary.new" "$root/vendor/$binary"
done
cp "$source/deploy/bootstrap.py" "$root/deploy/bootstrap.py"
python3 "$root/deploy/bootstrap.py" "$1"
# Only this product's units are restarted after replacing their executables.
systemctl restart link-network link-server link-peer
"$root/bin/link-server" -data "$root/data" -command join -role admin
echo 'Initial join credential: /ROOT/Link/data/join-code.txt (valid for 15 minutes).'
