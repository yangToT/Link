"""Run with python deploy/test_fetch_clients.py; no network or system changes."""
import hashlib
import io
import pathlib
import tempfile
import unittest
from unittest.mock import patch
from fetch_clients import download


class DownloadTests(unittest.TestCase):
    def test_verified_cache_and_failed_replacement(self):
        with tempfile.TemporaryDirectory() as directory:
            target = pathlib.Path(directory) / 'archive.zip'
            payload = b'verified archive'
            expected = hashlib.sha256(payload).hexdigest()
            with patch('urllib.request.urlopen', return_value=io.BytesIO(payload)) as request:
                download('https://example.invalid/archive', target, expected)
                request.assert_called_once()
            with patch('urllib.request.urlopen', side_effect=AssertionError('cache must work offline')):
                download('https://example.invalid/archive', target, expected)
            target.write_bytes(b'old cache')
            with patch('urllib.request.urlopen', return_value=io.BytesIO(b'tampered')):
                with self.assertRaisesRegex(RuntimeError, 'checksum mismatch'):
                    download('https://example.invalid/archive', target, expected)
            self.assertEqual(target.read_bytes(), b'old cache')
            self.assertFalse(target.with_name('archive.zip.partial').exists())
            with patch('urllib.request.urlopen', side_effect=OSError('offline')):
                with self.assertRaises(OSError):
                    download('https://example.invalid/archive', target, expected)
            self.assertEqual(target.read_bytes(), b'old cache')


if __name__ == '__main__':
    unittest.main()