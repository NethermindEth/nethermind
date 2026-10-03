# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
import hashlib
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tarfile
import tempfile
import unittest
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).with_name('spin-diagnostic')))
import archive_private as module


class ArchiveTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.base = Path(self.temporary.name).resolve()
        self.root = self.base / 'spin-diagnostic-123-1'
        self.root.mkdir()
        (self.root / 'outputs').mkdir()
        (self.root / 'preparation.json').write_text(json.dumps({'status': 'PREPARED_NOT_MEASURED', 'outputs': str(self.root / 'outputs')}))
        (self.root / 'outputs' / 'SECRET_PROCESS.data').write_bytes(b'RAW_SECRET_PROCESS_AND_TRACE')
        self.output = self.base / 'public' / 'encrypted'
        self.cert = self.base / 'recipient.crt'
        self.cert.write_bytes(b'PUBLIC_TEST_CERTIFICATE')
        self.openssl = self.base / 'openssl'
        self.openssl.write_bytes(b'TEST_EXECUTABLE')
        self.der = b'FIXTURE_DER'
        self.commands, self.archived = [], {}
        for context in (
            patch.dict(os.environ, {'RUNNER_TEMP': str(self.base), 'GITHUB_RUN_ID': '123', 'GITHUB_RUN_ATTEMPT': '1'}),
            patch.object(module, 'private_directory'),
            patch.object(module, 'CERTIFICATE_SHA256', hashlib.sha256(self.der).hexdigest()),
            patch.object(module.subprocess, 'run', side_effect=self.fake_run),
            patch.object(module.shutil, 'disk_usage', return_value=Mock(free=32 * 1024 * module.MIB)),
        ):
            context.start()
            self.addCleanup(context.stop)
        if os.name == 'nt':
            # Windows lstat/fstat ctime semantics differ; production requires POSIX.
            original_identity = module.identity
            context = patch.object(module, 'identity', side_effect=lambda info: original_identity(info)[:-1] + (0,))
            context.start()
            self.addCleanup(context.stop)

    def fake_run(self, command, **kwargs):
        self.commands.append(command)
        self.assertNotIn('shell', kwargs)
        if command[1] == 'x509':
            return subprocess.CompletedProcess(command, 0, self.der, b'')
        self.assertEqual(kwargs['timeout'], 300)
        self.assertEqual(command[1:4], ['cms', '-encrypt', '-binary'])
        plaintext = Path(command[command.index('-in') + 1])
        self.assertFalse(plaintext.is_relative_to(self.output))
        with tarfile.open(fileobj=io.BytesIO(plaintext.read_bytes()), mode='r:gz') as archive:
            self.archived = {item.name: archive.extractfile(item).read() for item in archive if item.isfile()}
        Path(command[command.index('-out') + 1]).write_bytes(b'FAKE_ENCRYPTED_CIPHERTEXT')
        return subprocess.CompletedProcess(command, 0, b'', b'')

    def execute(self):
        return module.archive(self.root, self.output, self.cert, self.openssl)

    def test_exact_owned_archive_without_public_plaintext_or_source_deletion(self):
        result = self.execute()
        self.assertEqual(result['status'], 'ENCRYPTED_NOT_DECRYPTION_VERIFIED')
        self.assertEqual(self.archived['outputs/SECRET_PROCESS.data'], b'RAW_SECRET_PROCESS_AND_TRACE')
        self.assertTrue((self.root / 'outputs' / 'SECRET_PROCESS.data').exists())
        self.assertEqual({item.name for item in self.output.iterdir()}, {'raw.tar.gz.cms', 'archive.json', 'recipient.crt'})
        public = b''.join(item.read_bytes() for item in self.output.iterdir())
        self.assertNotIn(b'SECRET_PROCESS', public)
        self.assertNotIn(b'RAW_SECRET', public)
        self.assertFalse(list(self.base.glob('spin-raw-private-*')))
        command = self.commands[-1]
        for value in ('-aes-256-cbc', 'rsa_padding_mode:oaep', 'rsa_oaep_md:sha256', 'rsa_mgf1_md:sha256'):
            self.assertIn(value, command)
        self.assertEqual(result['source_file_count'], 2)

    def test_foreign_root_nested_output_existing_output_and_owner_mismatch_rejected(self):
        foreign = self.base / 'spin-diagnostic-124-1'
        foreign.mkdir()
        for source, output in ((foreign, self.output), (self.root, self.root / 'public')):
            with self.subTest(source=source, output=output), self.assertRaises(module.ArchiveError):
                module.archive(source, output, self.cert, self.openssl)
        self.output.mkdir(parents=True)
        with self.assertRaises(module.ArchiveError):
            self.execute()
        self.output.rmdir()
        (self.root / 'preparation.json').write_text('{"outputs":"FOREIGN"}')
        with self.assertRaises(module.ArchiveError):
            self.execute()
        self.assertEqual(self.commands, [])

    def test_certificate_pin_failure_never_creates_archive(self):
        with patch.object(module, 'CERTIFICATE_SHA256', '0' * 64), self.assertRaises(module.ArchiveError):
            self.execute()
        self.assertFalse(self.output.exists())
        self.assertEqual(len(self.commands), 1)

    def test_encryption_failure_keeps_raw_and_has_no_ready_manifest(self):
        def fail(command, **kwargs):
            if command[1] == 'x509':
                return subprocess.CompletedProcess(command, 0, self.der, b'')
            raise subprocess.CalledProcessError(1, command, stderr=b'PRIVATE_SECRET')
        with patch.object(module.subprocess, 'run', side_effect=fail), self.assertRaises(subprocess.CalledProcessError):
            self.execute()
        self.assertFalse((self.output / 'archive.json').exists())
        self.assertTrue((self.root / 'outputs' / 'SECRET_PROCESS.data').exists())
        self.assertFalse(list(self.base.glob('spin-raw-private-*')))

    def test_bounds_and_source_change_are_rejected(self):
        with patch.object(module, 'MAX_BYTES', 1), self.assertRaises(module.ArchiveError):
            self.execute()
        self.assertFalse(self.output.exists())
        original = module.scan
        calls = 0
        def changing(root):
            nonlocal calls
            calls += 1
            if calls == 2:
                (root / 'outputs' / 'late.data').write_bytes(b'late')
            return original(root)
        with patch.object(module, 'scan', side_effect=changing), self.assertRaises(module.ArchiveError):
            self.execute()
        self.assertFalse((self.output / 'archive.json').exists())

    def test_archive_space_boundaries_on_same_and_distinct_devices(self):
        self.assertEqual(module.MAX_BYTES, 8 * 1024 * module.MIB)
        bound = module.archive_copy_bound(module.MAX_BYTES, module.MAX_FILES)
        self.assertGreater(bound, module.MAX_BYTES + module.MAX_FILES * 8192)
        for same in (True, False):
            temporary, output = Mock(), Mock()
            temporary.stat.return_value.st_dev = 1
            output.stat.return_value.st_dev = 1 if same else 2
            required = (2 if same else 1) * bound + module.STORAGE_RESERVE
            for free in (required - 1, required, required + 1):
                with self.subTest(same=same, free=free), \
                        patch.object(module.shutil, 'disk_usage', return_value=Mock(free=free)) as disk:
                    if free < required:
                        with self.assertRaisesRegex(module.ArchiveError, 'PRIVATE_ARCHIVE_SPACE'):
                            module.check_archive_space(temporary, output, bound)
                    else:
                        module.check_archive_space(temporary, output, bound)
                        self.assertEqual(disk.call_count, 1 if same else 2)
            if not same:
                with patch.object(module.shutil, 'disk_usage', side_effect=[
                        Mock(free=required), Mock(free=bound + module.STORAGE_RESERVE - 1)]), \
                        self.assertRaisesRegex(module.ArchiveError, 'ENCRYPTED_ARCHIVE_SPACE'):
                    module.check_archive_space(temporary, output, bound)

    def test_insufficient_space_preserves_source_and_never_encrypts(self):
        with patch.object(module.shutil, 'disk_usage', return_value=Mock(free=0)), \
                self.assertRaisesRegex(module.ArchiveError, 'PRIVATE_ARCHIVE_SPACE'):
            self.execute()
        self.assertEqual(len(self.commands), 1)
        self.assertTrue((self.root / 'outputs' / 'SECRET_PROCESS.data').exists())
        self.assertFalse((self.output / 'archive.json').exists())
        self.assertFalse(list(self.base.glob('spin-raw-private-*')))

    def test_space_is_rechecked_after_compression(self):
        with patch.object(module, 'check_archive_space') as initial, \
                patch.object(module.shutil, 'disk_usage', return_value=Mock(free=0)), \
                self.assertRaisesRegex(module.ArchiveError, 'ENCRYPTED_ARCHIVE_SPACE'):
            self.execute()
        initial.assert_called_once()
        self.assertEqual(len(self.commands), 1)
        self.assertTrue((self.root / 'outputs' / 'SECRET_PROCESS.data').exists())
        self.assertFalse((self.output / 'raw.tar.gz.cms').exists())
        self.assertFalse(list(self.base.glob('spin-raw-private-*')))

    def test_incompressible_data_and_long_pax_paths_fit_copy_bound(self):
        # Exercise actual gzip/tar serialization without depending on filesystem path limits.
        import gzip
        data = os.urandom(65537)
        stream = io.BytesIO()
        with gzip.GzipFile(fileobj=stream, mode='wb', compresslevel=1) as compressed, \
                tarfile.open(fileobj=compressed, mode='w|') as archive:
            item = tarfile.TarInfo('x' * 4096)
            item.size = len(data)
            archive.addfile(item, io.BytesIO(data))
        self.assertLess(len(stream.getvalue()), module.archive_copy_bound(len(data), 1))

    def test_archive_copy_overflow_never_gets_ready_manifest(self):
        for cipher_overflow in (False, True):
            with self.subTest(cipher_overflow=cipher_overflow):
                self.output = self.base / ('oversized-cipher' if cipher_overflow else 'oversized-plain')
                bound = 4096 if cipher_overflow else 1
                def oversized(command, **kwargs):
                    result = self.fake_run(command, **kwargs)
                    if command[1] == 'cms':
                        Path(command[command.index('-out') + 1]).write_bytes(b'x' * (bound + 1))
                    return result
                with patch.object(module, 'archive_copy_bound', return_value=bound), \
                        patch.object(module.subprocess, 'run', side_effect=oversized), \
                        self.assertRaisesRegex(module.ArchiveError, 'ARCHIVE_COPY_SIZE_LIMIT'):
                    self.execute()
                self.assertTrue((self.root / 'outputs' / 'SECRET_PROCESS.data').exists())
                self.assertFalse((self.output / 'archive.json').exists())
                self.assertFalse(list(self.base.glob('spin-raw-private-*')))

    def test_hardlink_rejected(self):
        try:
            os.link(self.root / 'outputs' / 'SECRET_PROCESS.data', self.root / 'linked.data')
        except OSError as error:
            self.skipTest(type(error).__name__)
        with self.assertRaises(module.ArchiveError):
            self.execute()
        self.assertFalse(self.output.exists())

    def test_inaccessible_tree_is_not_silently_omitted(self):
        def inaccessible(root, *, followlinks, onerror):
            onerror(PermissionError('PRIVATE_DIRECTORY_NAME'))
        with patch.object(module.os, 'walk', side_effect=inaccessible), self.assertRaises(module.ArchiveError):
            self.execute()
        self.assertFalse(self.output.exists())

    def test_cli_does_not_print_private_subprocess_diagnostics(self):
        stderr = io.StringIO()
        from contextlib import redirect_stderr
        with patch('sys.argv', ['archive_private.py', '--input-dir', str(self.root),
                                '--output-dir', str(self.output), '--certificate', str(self.cert)]), \
                patch.object(module, 'archive', side_effect=subprocess.CalledProcessError(1, ['SECRET_ARGUMENT'], stderr=b'PRIVATE_SECRET')), \
                redirect_stderr(stderr), self.assertRaises(SystemExit) as error:
            module.main()
        self.assertEqual(error.exception.code, 1)
        self.assertEqual(stderr.getvalue(), 'ARCHIVE_OR_ENCRYPTION_FAILED\n')


if __name__ == '__main__':
    unittest.main()
