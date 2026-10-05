"""Release only the independently audited reservation; preserve all raw evidence."""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import sys
import restore_absent as restored


def main():
    os.umask(0o077)
    assert os.environ.get('RUNNER_NAME')=='reproducible-benchmarks'
    root,lock,repository=restored.ROOT,restored.LOCK,restored.REPOSITORY
    release_run=os.environ['GITHUB_RUN_ID']
    assert release_run!=restored.RUN
    assert hashlib.sha256(Path(restored.__file__).read_bytes()).hexdigest()=='d5aeac66c7954138d51e8edfa88355e9fe0b63c0e65b5664eb8076fa8063b21a'
    assert hashlib.sha256(Path(restored.recovery.__file__).read_bytes()).hexdigest()=='0508a53793c4f2fd06775f6343a3218f1586780c1c0d8723f19a523f492a1882'
    sys.path.insert(0,str(repository/'scripts/rpc-capability'))
    import guard
    assert hashlib.sha256(Path(guard.__file__).read_bytes()).hexdigest()=='4b54bdb3fcf3bb9d93e112ee56cf004c160d00427d29879c809281b79498ad95'
    for relative,digest in json.loads((repository/'scripts/rpc-capability/native/source-pins.json').read_text()).items():
        assert hashlib.sha256((repository/relative).read_bytes()).hexdigest()==digest
    os.environ.update(GITHUB_RUN_ID=restored.RUN,GITHUB_RUN_ATTEMPT='1',RPC_PRIVATE_STORAGE_ROOT=str(root),
                      RPC_PRIVATE_SHARED_SCRATCH_ROOT=str(root.parent))
    guard.owned(root,lock)
    result=root/'absent-recovery-result.json'
    guard.read(result)
    assert guard.digest(result)=='6d56c201bc988ce7feb5c833637bfb962bade03717f78420993ef197412deea9'
    spec=importlib.util.spec_from_file_location('private_audit',repository/'scripts/rpc-bench/private_audit.py')
    audit=importlib.util.module_from_spec(spec);spec.loader.exec_module(audit)
    audit.context();audit.storage_context()
    restored.quiescent();restored.no_foreign()
    assert not audit.mount_records(root)
    journal=root/'scratch/cpu-state/cpu-sysfs.orig'
    assert not journal.is_symlink() and (not journal.exists() or journal.stat().st_size==0)
    originals=guard.read(root/'absent-recovery-originals.json')
    assert originals==guard.read(root/'absent-recovery-readback.json')
    assert set(originals)=={f'/sys/devices/system/cpu/cpufreq/policy{i}/{name}' for i in range(16) for name in ('scaling_governor','scaling_max_freq')}
    for path,value in originals.items():
        assert value==('performance' if path.endswith('/scaling_governor') else '5389000')
        assert Path(path).resolve(strict=True)==Path(path) and Path(path).read_text().strip()==value
    assert guard.digest(root/'absent-recovery-db-final.txt')=='c05b88644ecedf04dcf484f8c7cb90ed2291ad08e816b069bc6f6ab0adf086cd'
    hold=root/'native-ownership-hold.json'
    assert guard.read(hold)=={'schema':1,'ownership':'UNKNOWN_HOLD_NO_CLEANUP_OR_NEXT_DISPATCH'}
    data=hold.read_bytes()
    restored.quiescent();restored.no_foreign();guard.owned(root,lock)
    assert hold.read_bytes()==data
    details={'target_failed_run':restored.RUN,'accepted_recovery_run':'37313035638','release_run':release_run,
             'accepted_recovery_sha256':guard.digest(result),'hold_sha256':hashlib.sha256(data).hexdigest(),
             'raw_sources_retained':True,'failed_native_result_unchanged':True,'normal_shutdown':'UNKNOWN'}
    with (root/'native-ownership-hold.resolved.json').open('xb') as target:
        target.write(data);target.flush();os.fsync(target.fileno())
    with (root/'resource-release-intent.json').open('x') as target:
        json.dump(details|{'status':'INTENT_RECONCILE_BEFORE_ANY_RETRY'},target,indent=2)
        target.flush();os.fsync(target.fileno())
    with (root/'resource-release.json').open('x+') as target:
        json.dump(details|{'status':'RELEASE_NOT_COMPLETE'},target,indent=2)
        target.flush();os.fsync(target.fileno())
        directory=os.open(root,os.O_RDONLY|os.O_DIRECTORY)
        try:os.fsync(directory)
        finally:os.close(directory)
        guard.owned(root,lock);assert hold.read_bytes()==data
        hold.unlink()
        guard.owned(root,lock)
        (lock/'owner.json').unlink();lock.rmdir()
        restored.quiescent();restored.no_foreign()
        assert not lock.exists() and not hold.exists() and not audit.mount_records(root)
        audit.context();audit.storage_context()
        assert all(Path(path).read_text().strip()==value for path,value in originals.items())
        target.seek(0);target.truncate()
        json.dump(details|{'status':'OWNED_RESERVATION_RELEASED_AFTER_INDEPENDENT_AUDIT'},target,indent=2)
        target.flush();os.fsync(target.fileno())
    print('OWNED_RESERVATION_RELEASED_AFTER_INDEPENDENT_AUDIT')


if __name__=='__main__':
    try:main()
    except Exception as error:
        print('RECOVERY_RELEASE_HOLD:'+type(error).__name__,file=sys.stderr)
        raise SystemExit(1)
