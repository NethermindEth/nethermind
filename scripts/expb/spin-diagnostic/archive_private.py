# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
"""Encrypt one owned Actions diagnostic root; never publish plaintext or delete source."""
import argparse
import gzip
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import stat
import subprocess
import tarfile
import tempfile

CERTIFICATE_SHA256 = '2ff22f6fc2345a1aea365f516db3b746ad8d09010c20cac4409c11d74517f6f6'
MIB = 1024 * 1024
MAX_BYTES = 8 * 1024 * MIB
MAX_FILES = 20000
STORAGE_RESERVE = 1024 * MIB


class ArchiveError(RuntimeError):
    pass


def archive_copy_bound(source_bytes, entry_count):
    # Includes 4096-byte PAX paths, file padding, tar end blocks and gzip expansion.
    tar_bytes = source_bytes + entry_count * 8192 + 10240
    return tar_bytes + tar_bytes // 100 + 2 * MIB


def check_archive_space(temporary, output, copy_bound):
    same_device = temporary.stat().st_dev == output.stat().st_dev
    required = (2 if same_device else 1) * copy_bound + STORAGE_RESERVE
    if shutil.disk_usage(temporary).free < required:
        raise ArchiveError('INSUFFICIENT_PRIVATE_ARCHIVE_SPACE')
    if not same_device and shutil.disk_usage(output).free < copy_bound + STORAGE_RESERVE:
        raise ArchiveError('INSUFFICIENT_ENCRYPTED_ARCHIVE_SPACE')


def digest_file(path):
    digest = hashlib.sha256()
    with path.open('rb') as stream:
        for chunk in iter(lambda: stream.read(MIB), b''):
            digest.update(chunk)
    return digest.hexdigest()


def private_directory(path):
    info = path.lstat()
    if not hasattr(os, 'getuid'):
        raise ArchiveError('POSIX_PRIVATE_ACCESS_CHECK_REQUIRED')
    if not stat.S_ISDIR(info.st_mode) or info.st_uid != os.getuid() or stat.S_IMODE(info.st_mode) != 0o700:
        raise ArchiveError('OWNED_PRIVATE_DIRECTORY_REQUIRED')


def identity(info):
    return (info.st_dev, info.st_ino, info.st_mode, info.st_nlink, info.st_size, info.st_mtime_ns, info.st_ctime_ns)


def scan(root):
    entries, total, files = {}, 0, 0
    def inaccessible(_):
        raise ArchiveError('SOURCE_TREE_INACCESSIBLE')
    for current, directories, names in os.walk(root, followlinks=False, onerror=inaccessible):
        for name in sorted(directories + names):
            path = Path(current) / name
            info = path.lstat()
            relative = path.relative_to(root).as_posix()
            if not (stat.S_ISDIR(info.st_mode) or stat.S_ISREG(info.st_mode)) or (stat.S_ISREG(info.st_mode) and info.st_nlink != 1):
                raise ArchiveError('LINK_OR_SPECIAL_SOURCE_REJECTED')
            if len(relative.encode('utf-8')) > 4096 or len(entries) >= MAX_FILES:
                raise ArchiveError('ARCHIVE_ENTRY_LIMIT')
            if stat.S_ISREG(info.st_mode):
                files += 1
                total += info.st_size
                if info.st_size < 0 or total > MAX_BYTES:
                    raise ArchiveError('ARCHIVE_BYTE_LIMIT')
            entries[relative] = identity(info)
    return entries, files, total


def archive(input_dir, output_dir, certificate, openssl='/usr/bin/openssl'):
    run_id, attempt = os.environ['GITHUB_RUN_ID'], os.environ['GITHUB_RUN_ATTEMPT']
    if not all(re.fullmatch(r'[1-9][0-9]{0,19}', value) for value in (run_id, attempt)):
        raise ArchiveError('INVALID_RUN_IDENTITY')
    temporary = Path(os.environ['RUNNER_TEMP']).resolve(strict=True)
    source = Path(input_dir).absolute()
    expected = temporary / ('spin-diagnostic-' + run_id + '-' + attempt)
    if source != expected:
        raise ArchiveError('EXACT_OWNED_RUN_ROOT_REQUIRED')
    private_directory(source)
    if source.resolve(strict=True) != expected:
        raise ArchiveError('SOURCE_ROOT_ALIAS_REJECTED')
    output = Path(output_dir).resolve()
    if output.exists() or output.is_relative_to(source) or source.is_relative_to(output):
        raise ArchiveError('NEW_SEPARATE_OUTPUT_DIRECTORY_REQUIRED')
    preparation_path = source / 'preparation.json'
    info = preparation_path.lstat()
    if not stat.S_ISREG(info.st_mode) or info.st_size > 4 * MIB:
        raise ArchiveError('PREPARATION_MISSING_OR_INVALID')
    preparation = json.loads(preparation_path.read_text(encoding='utf-8'))
    if preparation.get('status') != 'PREPARED_NOT_MEASURED' or preparation.get('outputs') != str(source / 'outputs'):
        raise ArchiveError('PREPARATION_OWNER_MISMATCH')
    cert, executable = Path(certificate).absolute(), Path(openssl).absolute()
    cert_info, executable_info = cert.lstat(), executable.lstat()
    if not stat.S_ISREG(cert_info.st_mode) or cert_info.st_size > 64 * 1024:
        raise ArchiveError('PUBLIC_CERTIFICATE_INVALID')
    if not stat.S_ISREG(executable_info.st_mode) or (hasattr(os, 'getuid') and executable_info.st_mode & 0o022):
        raise ArchiveError('OPENSSL_EXECUTABLE_INVALID')
    verified = subprocess.run([str(executable), 'x509', '-in', str(cert), '-outform', 'DER'],
                              check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=20)
    if len(verified.stdout) > 64 * 1024 or hashlib.sha256(verified.stdout).hexdigest() != CERTIFICATE_SHA256:
        raise ArchiveError('PUBLIC_CERTIFICATE_PIN_MISMATCH')
    entries, file_count, source_bytes = scan(source)
    output.mkdir(mode=0o700, parents=True, exist_ok=False)
    copy_bound = archive_copy_bound(source_bytes, len(entries))
    check_archive_space(temporary, output, copy_bound)
    with tempfile.TemporaryDirectory(prefix='spin-raw-private-', dir=temporary) as scratch:
        private_directory(Path(scratch))
        plaintext = Path(scratch) / 'raw.tar.gz'
        # File names and all raw observations exist only inside the private tar/ciphertext.
        with plaintext.open('xb') as raw, gzip.GzipFile(fileobj=raw, mode='wb', filename='', mtime=0, compresslevel=1) as zipped, tarfile.open(fileobj=zipped, mode='w|') as tar:
            for relative, observed in sorted(entries.items()):
                path = source / relative
                if identity(path.lstat()) != observed:
                    raise ArchiveError('SOURCE_CHANGED_DURING_ARCHIVE')
                item = tarfile.TarInfo(relative)
                item.mode = 0o700 if stat.S_ISDIR(observed[2]) else 0o600
                if stat.S_ISDIR(observed[2]):
                    item.type = tarfile.DIRTYPE
                    tar.addfile(item)
                else:
                    flags = os.O_RDONLY | getattr(os, 'O_NOFOLLOW', 0) | getattr(os, 'O_BINARY', 0)
                    with os.fdopen(os.open(path, flags), 'rb') as stream:
                        if identity(os.fstat(stream.fileno())) != observed:
                            raise ArchiveError('SOURCE_CHANGED_DURING_ARCHIVE')
                        item.size = observed[4]
                        tar.addfile(item, stream)
                        if identity(os.fstat(stream.fileno())) != observed:
                            raise ArchiveError('SOURCE_CHANGED_DURING_ARCHIVE')
        if scan(source)[0] != entries or identity(cert.lstat()) != identity(cert_info):
            raise ArchiveError('SOURCE_OR_CERTIFICATE_CHANGED')
        plaintext_sha, plaintext_bytes = digest_file(plaintext), plaintext.stat().st_size
        if plaintext_bytes > copy_bound:
            raise ArchiveError('ARCHIVE_COPY_SIZE_LIMIT')
        if shutil.disk_usage(output).free < plaintext_bytes + 2 * MIB + STORAGE_RESERVE:
            raise ArchiveError('INSUFFICIENT_ENCRYPTED_ARCHIVE_SPACE')
        cipher = output / 'raw.tar.gz.cms'
        subprocess.run([str(executable), 'cms', '-encrypt', '-binary', '-in', str(plaintext),
                        '-out', str(cipher), '-outform', 'DER', '-aes-256-cbc', '-recip', str(cert),
                        '-keyopt', 'rsa_padding_mode:oaep', '-keyopt', 'rsa_oaep_md:sha256',
                        '-keyopt', 'rsa_mgf1_md:sha256'], check=True, stdout=subprocess.PIPE,
                       stderr=subprocess.PIPE, timeout=300)
        if not cipher.is_file() or cipher.is_symlink() or cipher.stat().st_size == 0:
            raise ArchiveError('ENCRYPTED_OUTPUT_MISSING')
        if cipher.stat().st_size > copy_bound:
            raise ArchiveError('ARCHIVE_COPY_SIZE_LIMIT')
        if identity(cert.lstat()) != identity(cert_info) or identity(executable.lstat()) != identity(executable_info):
            raise ArchiveError('ENCRYPTION_TOOL_OR_CERTIFICATE_CHANGED')
        (output / 'recipient.crt').write_bytes(cert.read_bytes())
        manifest = {'schema': 1, 'status': 'ENCRYPTED_NOT_DECRYPTION_VERIFIED',
                    'run_id': int(run_id), 'run_attempt': int(attempt),
                    'source_file_count': file_count, 'source_bytes': source_bytes,
                    'plaintext': {'sha256': plaintext_sha, 'bytes': plaintext_bytes},
                    'ciphertext': {'file': cipher.name, 'sha256': digest_file(cipher), 'bytes': cipher.stat().st_size},
                    'certificate_der_sha256': CERTIFICATE_SHA256,
                    'certificate_pem_sha256': digest_file(output / 'recipient.crt'),
                    'format': 'CMS DER', 'content_cipher': 'AES-256-CBC',
                    'key_transport': 'RSA-OAEP SHA256 MGF1-SHA256',
                    'openssl_sha256': digest_file(executable),
                    'helper_sha256': digest_file(Path(__file__)),
                    'raw_source_deleted': False, 'linux_capability_verified': False}
        (output / 'archive.json').write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
        return manifest


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--input-dir', required=True)
    parser.add_argument('--output-dir', required=True)
    parser.add_argument('--certificate', required=True)
    parser.add_argument('--openssl', default='/usr/bin/openssl')
    args = parser.parse_args()
    try:
        result = archive(args.input_dir, args.output_dir, args.certificate, args.openssl)
    except ArchiveError as error:
        parser.exit(1, str(error) + '\n')
    except (OSError, ValueError, KeyError, TypeError, subprocess.SubprocessError):
        parser.exit(1, 'ARCHIVE_OR_ENCRYPTION_FAILED\n')
    print(json.dumps({'status': result['status'], 'source_file_count': result['source_file_count']}))
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
