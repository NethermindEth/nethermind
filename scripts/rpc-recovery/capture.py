"""Read-only evidence of one failed owned RPC attempt; no cleanup or restore."""
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import subprocess
import sys

OLD_RUN = '37317936571'
SHARED = Path('/mnt/sda/expb-data/rpc-bench-scratch')
STORAGE = SHARED / ('rpc-private-' + OLD_RUN + '-1')
LOCK = SHARED / 'rpc-native-capability.lock'


def write(path, value):
    with path.open('x') as stream:
        json.dump(value, stream, indent=2)


def main():
    os.umask(0o077)
    assert os.environ.get('RUNNER_NAME') == 'reproducible-benchmarks'
    repository = Path(__file__).resolve().parents[2]
    sys.path.insert(0, str(repository / 'scripts/rpc-bench/private_audit'))
    import archive_private as archive
    run, attempt = os.environ['GITHUB_RUN_ID'], os.environ['GITHUB_RUN_ATTEMPT']
    assert run != OLD_RUN and all(re.fullmatch('[1-9][0-9]*', v) for v in (run, attempt))
    for directory in (SHARED, STORAGE, LOCK):
        assert directory.resolve(strict=True) == directory
    archive.private_directory(STORAGE)
    archive.private_directory(LOCK)
    owner = json.loads((LOCK / 'owner.json').read_text())
    assert owner['run_id'] == OLD_RUN and owner['attempt'] == '1' and owner['storage'] == str(STORAGE)
    for key, directory in (('storage_identity', STORAGE), ('lock_identity', LOCK)):
        info = directory.stat()
        assert owner[key] == [info.st_dev, info.st_ino]
    retained = SHARED / ('rpc-recovery-' + run + '-' + attempt)
    retained.mkdir(mode=0o700)
    source = retained / ('spin-diagnostic-' + run + '-' + attempt)
    source.mkdir(mode=0o700)
    (source / 'outputs').mkdir(mode=0o700)
    write(source / 'preparation.json', {'status':'PREPARED_NOT_MEASURED','outputs':str(source / 'outputs'),
          'purpose':'READ_ONLY_FAILED_RPC_EVIDENCE','target_run':OLD_RUN})
    write(source / 'integration-owner.json', owner)
    mountinfo = Path('/proc/self/mountinfo').read_text()
    mount_targets = [Path(re.sub(r'\\([0-7]{3})', lambda m: chr(int(m[1],8)), line.split()[4])) for line in mountinfo.splitlines()]
    (source / 'mountinfo.txt').write_text(mountinfo)
    copied = {}
    def copy(path):
        assert path.resolve(strict=True) == path and path.is_relative_to(STORAGE)
        assert not any(path == target or path.is_relative_to(target) for target in mount_targets if target.is_relative_to(STORAGE))
        before = archive.identity(path.lstat())
        assert stat.S_ISREG(before[2]) and before[3] == 1 and before[4] <= 256 * 1024 * 1024
        relative = path.relative_to(STORAGE)
        output = source / 'retained' / relative
        output.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
        with path.open('rb') as incoming, output.open('xb') as target:
            assert archive.identity(os.fstat(incoming.fileno())) == before
            count = 0
            for chunk in iter(lambda: incoming.read(1024 * 1024), b''):
                count += len(chunk)
                assert count <= before[4]
                target.write(chunk)
            assert archive.identity(os.fstat(incoming.fileno())) == before
        assert archive.identity(path.lstat()) == before and count == before[4]
        copied[str(relative)] = {'identity':before,'sha256':archive.digest_file(output)}
    for path in STORAGE.iterdir():
        if path.is_file(): copy(path)
    for directory in (STORAGE / ('spin-diagnostic-' + OLD_RUN + '-1'), STORAGE / 'state', STORAGE / 'scratch/cpu-state',
                      STORAGE / 'scratch/warmup-cell/default/nativecap'):
        if directory.exists():
            for path in directory.rglob('*'):
                if path.is_file(): copy(path)
    optional = {}
    for relative in ('scratch/jsonbench/jsonbench-tool.log', 'scratch/jsonbench/io/out/summary.json',
                     'scratch/jsonbench/io/benchmark.yaml', 'scratch/jsonbench/io/clients.yaml'):
        path = STORAGE / relative
        optional[relative] = 'NOT_PRESENT'
        if path.exists():
            copy(path)
            optional[relative] = 'STABLE_PRIVATE_COPY'
    optional['scratch/warmup-cell/default/nativecap'] = (
        'DIRECTORY_PRESENT' if (STORAGE / 'scratch/warmup-cell/default/nativecap').exists() else 'NOT_PRESENT')
    write(source / 'optional-source-presence.json', optional)
    assert len(copied) <= 5000
    write(source / 'copied.json', copied)
    docker = subprocess.check_output(['docker','ps','-a','--no-trunc','--format','{{json .}}'], timeout=30)
    (source / 'docker-containers.jsonl').write_bytes(docker)
    labelled = subprocess.check_output(['docker','ps','-aq','--no-trunc','--filter','label=codex.rpc.private-run='+OLD_RUN+'-1'], timeout=30)
    (source / 'owned-container-ids.txt').write_bytes(labelled)
    for cid in labelled.decode().splitlines():
        assert re.fullmatch('[0-9a-f]{64}', cid)
        (source / ('container-'+cid+'.json')).write_bytes(subprocess.check_output(['docker','inspect',cid],timeout=30))
    OLD_CID = (STORAGE / ('spin-diagnostic-' + OLD_RUN + '-1') / 'capability-node-cid.log').read_text().strip()
    assert re.fullmatch('[0-9a-f]{64}', OLD_CID)
    matches, inaccessible = [], []
    tokens = [str(STORAGE).encode(), ('rpc-capability-operator-'+OLD_RUN+'-1').encode(), ('rpc-capability-harness-'+OLD_RUN+'-1').encode(), OLD_CID.encode()]
    for process in Path('/proc').iterdir():
        if not process.name.isdecimal() or int(process.name) == os.getpid(): continue
        try:
            cmd = (process/'cmdline').read_bytes()
            environment = (process/'environ').read_bytes()
            references=[cmd,environment,(process/'cgroup').read_bytes()]
            if cmd:
                references.append(os.readlink(process/'cwd').encode())
                for descriptor in (process/'fd').iterdir():
                    try:references.append(os.readlink(descriptor).encode())
                    except FileNotFoundError:pass
            if any(token in item for token in tokens for item in references):
                matches.append({'pid':int(process.name),'stat':(process/'stat').read_text(),'cmdline':cmd.decode(errors='replace'),
                                'cgroup':(process/'cgroup').read_text()})
        except (FileNotFoundError, ProcessLookupError):
            continue
        except PermissionError:
            inaccessible.append(int(process.name))
    write(source/'process-matches.json', {'matches':matches,'inaccessible':inaccessible})
    journal = STORAGE/'scratch/cpu-state/cpu-sysfs.orig'
    cpu = []
    if journal.exists():
        for line in journal.read_text().splitlines():
            path, original = line.split('\t',1)
            assert path.startswith('/sys/devices/system/cpu/')
            cpu.append({'path':path,'original':original,'current':Path(path).read_text().strip()})
    write(source/'cpu-current.json', cpu)
    write(source/'capture.json', {'target_run':OLD_RUN,'mutated_target':False,'copied_count':len(copied),
          'process_matches':len(matches),'inaccessible_processes':len(inaccessible),'current_source_head':os.environ['GITHUB_SHA']})
    assert json.loads((LOCK/'owner.json').read_text()) == owner
    for key, directory in (('storage_identity', STORAGE), ('lock_identity', LOCK)):
        assert directory.resolve(strict=True) == directory
        info = directory.stat()
        assert owner[key] == [info.st_dev, info.st_ino]
    public = retained/'public'
    public.mkdir(mode=0o700)
    os.environ['RUNNER_TEMP'] = str(retained)
    archive.archive(source, public/'encrypted', repository/'scripts/rpc-bench/private_audit/recipient.crt')
    with Path(os.environ['GITHUB_OUTPUT']).open('a') as output: output.write('public='+str(public)+'\n')
    print(json.dumps({'status':'READ_ONLY_EVIDENCE_ENCRYPTED','target_run':OLD_RUN,'copied_files':len(copied),
                     'process_matches':len(matches),'inaccessible_processes':len(inaccessible),'target_mutation':False}))


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        print('READ_ONLY_CAPTURE_FAILED:' + type(error).__name__, file=sys.stderr)
        raise SystemExit(1)
