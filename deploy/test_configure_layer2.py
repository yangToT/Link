import runpy
from pathlib import Path
import unittest

module = runpy.run_path(str(Path(__file__).with_name('configure-layer2.py')))


class ConfigPasswordTests(unittest.TestCase):
    def test_upstream_legacy_password_format(self):
        encode = module['softether_password_hash']
        self.assertEqual(encode(b'').hex(), 'f96cea198ad1dd5617ac084a3d92c6107708c0ef')
        self.assertEqual(encode(b'abc').hex(), '0164b8a914cd2a5e74c4f7ff082c4d97f1edf880')
        self.assertEqual(encode(b'abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq').hex(),
                         'd2516ee1acfa5baf33dfc1c471e438449ef134c8')


if __name__ == '__main__':
    unittest.main()
