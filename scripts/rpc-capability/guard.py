"""Permit public export/release only after verified owned native success."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import stat

SHARED = Path('/mnt/sda/expb-data/rpc-bench-scratch')
LOCK_NAME = 'rpc-native-capability.lock'
REQUIRED_CALLS = {'preflight','prepare','cpu-attempt','cpu-apply','start','node-cid','container-pin','image-pin',
                  'warm','main','stop','node','teardown','restore-state','cpu-restore','finalize'}


def read(path):
    info = path.lstat()
    if not stat.S_ISREG(info.st_mode) or info.st_nlink != 1 or path.resolve(strict=True) != path or info.st_size > 1024 * 1024:
        raise ValueError('OWNED_JSON_REQUIRED')
    return json.loads(path.read_text())


def digest(path):
    result = hashlib.sha256()
    with path.open('rb') as source:
        for block in iter(lambda: source.read(1024 * 1024), b''):
            result.update(block)
    return result.hexdigest()


def directory(path):
    info = path.lstat()
    if path.resolve(strict=True) != path or not stat.S_ISDIR(info.st_mode):
        raise ValueError('CANONICAL_DIRECTORY_REQUIRED')
    if os.name == 'posix' and (info.st_uid != os.getuid() or stat.S_IMODE(info.st_mode) != 0o700):
        raise ValueError('PRIVATE_OWNED_DIRECTORY_REQUIRED')
    return [info.st_dev, info.st_ino]


def run_identity():
    values = [os.environ['GITHUB_RUN_ID'], os.environ['GITHUB_RUN_ATTEMPT']]
    if not all(re.fullmatch('[1-9][0-9]{0,19}', value) for value in values):
        raise ValueError('RUN_IDENTITY_REQUIRED')
    return values


def paths():
    run, attempt = run_identity()
    return SHARED / ('rpc-private-' + run + '-' + attempt), SHARED / LOCK_NAME


def owned(storage, lock):
    run, attempt = run_identity()
    if (storage, lock) != paths():
        raise ValueError('EXACT_OWNED_PATH_REQUIRED')
    expected = {'schema':1, 'run_id':run, 'attempt':attempt, 'storage':str(storage),
                'storage_identity':directory(storage), 'lock_identity':directory(lock)}
    if read(lock / 'owner.json') != expected:
        raise ValueError('FOREIGN_OWNER_MARKER')
    return expected


def success(storage, lock):
    owned(storage, lock)
    if (storage / 'native-ownership-hold.json').exists() or (storage / 'native-ownership-hold.json').is_symlink():
        raise ValueError('OWNERSHIP_HOLD')
    value = read(storage / 'native-capability.json')
    expected = {'schema':1, 'scope':'NATIVE_LIFECYCLE_ONLY', 'capability':'PASS', 'full_abba_quality':'FAIL',
                'timing_result':False, 'pooling_eligibility':'UNKNOWN', 'raw_sources_retained':True,
                'archive_ready':True, 'ownership':'COMMANDS_CLOSED'}
    if set(value) != set(expected) | {'outcomes'} or any(value.get(key) != item for key,item in expected.items()):
        raise ValueError('NATIVE_SUCCESS_REQUIRED')
    calls = value.get('outcomes', {})
    if set(calls) != REQUIRED_CALLS or any(type(code) is not int or code != (1 if name == 'finalize' else 0) for name,code in calls.items()):
        raise ValueError('CLOSED_SUCCESSFUL_CALLS_REQUIRED')
    run, attempt = run_identity()
    root = storage / ('spin-diagnostic-' + run + '-' + attempt)
    integration = read(storage / 'integration-freeze.json')
    if read(root / 'integration-freeze.json') != integration or read(root / 'prospective-expected-pins.json') != integration['expected_pins']:
        raise ValueError('ARCHIVED_PROSPECTIVE_PINS_REQUIRED')
    quality = read(root / 'audit-status.json')
    if (quality.get('quality') != 'FAIL' or quality.get('expected_arms') != 4 or quality.get('observed_arms') != 1
            or quality.get('valid_arms') != 1 or quality.get('pooling_eligibility') != 'UNKNOWN'):
        raise ValueError('EXPECTED_PARTIAL_QUALITY_REQUIRED')
    public = storage / ('rpc-private-public-' + run + '-' + attempt)
    directory(public)
    if {p.name for p in public.iterdir()} - {'encrypted','audit-status.json','native-capability.json'}:
        raise ValueError('UNEXPECTED_PUBLIC_FILE')
    if read(public / 'audit-status.json') != quality:
        raise ValueError('PUBLIC_QUALITY_DIFFERS')
    encrypted = public / 'encrypted'
    directory(encrypted)
    if {p.name for p in encrypted.iterdir()} != {'archive.json','raw.tar.gz.cms','recipient.crt'}:
        raise ValueError('UNEXPECTED_ENCRYPTED_FILE')
    for path in encrypted.iterdir():
        info=path.lstat()
        if not stat.S_ISREG(info.st_mode) or info.st_nlink != 1 or path.resolve(strict=True) != path:
            raise ValueError('PUBLIC_LINK_OR_SPECIAL_FILE')
    archive = read(encrypted / 'archive.json'); cipher = encrypted / 'raw.tar.gz.cms'
    if (archive.get('status') != 'ENCRYPTED_NOT_DECRYPTION_VERIFIED' or archive.get('run_id') != int(run)
            or archive.get('run_attempt') != int(attempt) or archive.get('raw_source_deleted') is not False
            or archive['ciphertext']['file'] != cipher.name or not cipher.stat().st_size
            or archive['ciphertext']['bytes'] != cipher.stat().st_size or archive['ciphertext']['sha256'] != digest(cipher)
            or archive['certificate_pem_sha256'] != digest(encrypted / 'recipient.crt')):
        raise ValueError('ARCHIVE_IDENTITY_REQUIRED')
    return public, dict(value)


def publish(storage, lock):
    public, safe = success(storage, lock)
    with (public / 'native-capability.json').open('x') as target:
        json.dump(safe, target, indent=2)
    with Path(os.environ['GITHUB_OUTPUT']).open('a') as output:
        output.write('public=' + str(public) + '\n')
        output.write('ready=true\n')


def release(storage, lock):
    success(storage, lock)
    artifact = os.environ['UPLOADED_ARTIFACT_ID']; checksum = os.environ['UPLOADED_ARTIFACT_DIGEST']
    if not re.fullmatch('[1-9][0-9]*', artifact) or not re.fullmatch('[0-9a-f]{64}', checksum):
        raise ValueError('SUCCESSFUL_UPLOAD_RECEIPT_REQUIRED')
    with (storage / 'upload-receipt.json').open('x') as target:
        json.dump({'artifact_id':artifact,'artifact_digest':checksum,'private_sources_retained':True},target)
    owned(storage, lock)
    (lock / 'owner.json').unlink()
    lock.rmdir()


if __name__ == '__main__':
    parser=argparse.ArgumentParser();parser.add_argument('action',choices=('publish','release'));args=parser.parse_args()
    os.umask(0o077)
    try:
        if os.name != 'posix':raise ValueError('POSIX_REQUIRED')
        storage,lock=paths()
        (publish if args.action == 'publish' else release)(storage,lock)
    except Exception as error:
        print('NATIVE_GUARD_HOLD:' + type(error).__name__)
        raise SystemExit(1)
