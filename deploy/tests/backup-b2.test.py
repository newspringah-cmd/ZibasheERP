"""Isolated synthetic backup tests; no production data, credentials or network."""
import gzip
import hashlib
import importlib.util
from pathlib import Path
import os
import sys
import tempfile
import time
import types
import unittest
from unittest.mock import patch

modules = {name: types.ModuleType(name) for name in ['boto3', 'botocore', 'botocore.config', 'boto3.s3', 'boto3.s3.transfer']}
modules['botocore.config'].Config = lambda **kw: kw
modules['boto3.s3.transfer'].TransferConfig = lambda **kw: kw
spec = importlib.util.spec_from_file_location('backup_b2', Path(__file__).resolve().parents[1] / 'backup-b2.py')
backup = importlib.util.module_from_spec(spec)
with patch.dict(sys.modules, modules):
    spec.loader.exec_module(backup)


class Storage:
    def __init__(self):
        self.objects = {}
        self.corrupt_metadata = False

    def upload_file(self, path, bucket, key, ExtraArgs, Config):
        self.objects[key] = {'ContentLength': Path(path).stat().st_size, 'Metadata': dict(ExtraArgs['Metadata'])}
        if self.corrupt_metadata:
            self.objects[key]['Metadata']['original-sha256'] = 'wrong'

    def head_object(self, Bucket, Key):
        return self.objects[Key]


class BackupTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.directory = Path(self.temp.name)
        self.original = self.directory / 'full-test.bak'
        self.payload = bytes(range(256)) * 5000
        self.original.write_bytes(self.payload)
        self.storage = Storage()
        self.config = {'bucket': 'synthetic-test-only'}
        self.key = backup.PREFIX + 'full/' + self.original.name

    def test_lossless_compression_preserves_original_and_metadata(self):
        packed = backup.compress_backup(self.original)
        self.assertEqual(gzip.decompress(packed.read_bytes()), self.payload)
        self.assertEqual(self.original.read_bytes(), self.payload)
        self.assertLess(packed.stat().st_size, self.original.stat().st_size)
        self.assertEqual(packed.stat().st_mode & 0o777, 0o600)
        backup.upload(self.config, self.storage, packed, self.key + '.gz', original=self.original)
        metadata = self.storage.objects[self.key + '.gz']['Metadata']
        self.assertEqual(metadata['original-sha256'], hashlib.sha256(self.payload).hexdigest())
        self.assertEqual(metadata['original-size'], str(len(self.payload)))
        self.assertEqual(metadata['sha256'], backup.sha256(packed))

    def test_upload_metadata_failure_preserves_both_local_files(self):
        packed = backup.compress_backup(self.original)
        self.storage.corrupt_metadata = True
        with self.assertRaises(RuntimeError):
            backup.upload(self.config, self.storage, packed, self.key + '.gz', original=self.original)
        self.assertTrue(self.original.exists())
        self.assertTrue(packed.exists())

    def test_local_retention_requires_matching_remote_original_checksum(self):
        packed = backup.compress_backup(self.original)
        backup.upload(self.config, self.storage, packed, self.key + '.gz', original=self.original)
        old = time.time() - 29 * 86400
        os.utime(self.original, (old, old))
        self.storage.objects[self.key + '.gz']['Metadata']['original-sha256'] = 'wrong'
        with patch.object(backup, 'HOST_DIR', self.directory):
            backup.cleanup_local(self.config, self.storage)
        self.assertTrue(self.original.exists())
        self.storage.objects[self.key + '.gz']['Metadata']['original-sha256'] = backup.sha256(self.original)
        with patch.object(backup, 'HOST_DIR', self.directory):
            backup.cleanup_local(self.config, self.storage)
        self.assertFalse(self.original.exists())
        self.assertFalse(packed.exists())

    def test_legacy_remote_backups_still_support_local_retention(self):
        backup.upload(self.config, self.storage, self.original, self.key)
        old = time.time() - 29 * 86400
        os.utime(self.original, (old, old))
        with patch.object(backup, 'HOST_DIR', self.directory):
            backup.cleanup_local(self.config, self.storage)
        self.assertFalse(self.original.exists())

    def test_recent_files_are_kept_even_when_remote_copy_is_verified(self):
        packed = backup.compress_backup(self.original)
        backup.upload(self.config, self.storage, packed, self.key + '.gz', original=self.original)
        with patch.object(backup, 'HOST_DIR', self.directory):
            backup.cleanup_local(self.config, self.storage)
        self.assertTrue(self.original.exists())
        self.assertTrue(packed.exists())


if __name__ == '__main__':
    unittest.main()
