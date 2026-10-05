"""Restore the preserved mount/CPU journal only after the old container is absent."""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import restore as recovery

ROOT,LOCK,STATE,CID,RUN,REPOSITORY=(getattr(recovery,n) for n in ('ROOT','LOCK','STATE','CID','RUN','REPOSITORY'))


def quiescent():
    recovery.scan_helpers(False)
    cids = (CID,recovery.TOOL_CID,recovery.VERSION_CID)
    for process in Path('/proc').iterdir():
        if not process.name.isdecimal() or int(process.name)==os.getpid():continue
        try:
            if any(cid.encode() in (process/'cgroup').read_bytes() or cid.encode() in (process/'cmdline').read_bytes() for cid in cids):
                raise ValueError('OLD_CONTAINER_PROCESS_OR_SHIM_REMAINS')
        except (FileNotFoundError,ProcessLookupError):pass


def no_foreign():
    entries=[json.loads(line) for line in recovery.output(['docker','ps','-a','--no-trunc','--format','{{json .}}']).splitlines()]
    assert not entries, 'DOCKER_MUST_BE_EMPTY_BEFORE_CPU_RESTORE'
    for label in ('expb','codex.rpc.private-run'):
        assert not recovery.output(['docker','ps','-aq','--filter','label='+label]).strip()


def main():
    assert os.environ.get('RUNNER_NAME')=='reproducible-benchmarks'
    os.umask(0o077)
    assert os.geteuid()==0 and os.environ['GITHUB_RUN_ID']!=RUN
    assert hashlib.sha256(Path(recovery.__file__).read_bytes()).hexdigest()=='0508a53793c4f2fd06775f6343a3218f1586780c1c0d8723f19a523f492a1882'
    for relative,digest in json.loads((REPOSITORY/'scripts/rpc-capability/native/source-pins.json').read_text()).items():
        assert hashlib.sha256((REPOSITORY/relative).read_bytes()).hexdigest()==digest
    sys.path.insert(0,str(REPOSITORY/'scripts/rpc-capability'))
    import guard
    actual_run=os.environ['GITHUB_RUN_ID']
    os.environ.update(GITHUB_RUN_ID=RUN,GITHUB_RUN_ATTEMPT='1',RPC_PRIVATE_STORAGE_ROOT=str(ROOT),
                      RPC_PRIVATE_SHARED_SCRATCH_ROOT=str(ROOT.parent))
    owner=guard.owned(ROOT,LOCK)
    assert owner['storage_identity']==[64512,41881671] and owner['lock_identity']==[64512,41881670]
    spec=importlib.util.spec_from_file_location('private_audit',REPOSITORY/'scripts/rpc-bench/private_audit.py')
    audit=importlib.util.module_from_spec(spec);spec.loader.exec_module(audit)
    def checked_output(command,timeout=20):
        data=recovery.output(command,timeout)
        if len(data)>1024*1024:raise ValueError('PROVENANCE_OUTPUT_SIZE')
        return data.decode('utf-8',errors='strict').strip()
    audit.command_output=checked_output
    audit.context();audit.storage_context()
    failure=guard.read(ROOT/'native-capability.json')
    assert failure['outcomes']=={'preflight':0,'prepare':0,'cpu-attempt':0,'cpu-apply':0,'start':0,'node-cid':0,'container-pin':0,'image-pin':0,'warm':1}
    assert failure['ownership']=='UNKNOWN_HOLD_NO_CLEANUP_OR_NEXT_DISPATCH'
    record=audit.owner_record(STATE,'node')
    assert record['name']=='rpcbench-sweep-nativecap-'+RUN
    assert record['image']=='nethermindeth/nethermind@sha256:a6e80b5688b383d2b60bc5c45ba2b4b200f80d3624ad9b7eddd662496bb8909b'
    assert record['image_id']=='sha256:7cba6a163a91e61e08ce3844e360b0d48745fdd7d2d18f7ee7c592ac59f1e8b7'
    assert (STATE/'node.cid').read_text().strip()==CID
    assert audit.owned_cid(STATE,'node',allow_absent=True) is None
    audit.name_available(record['name'])
    no_foreign();quiescent()
    mount=guard.read(STATE/'node-mount.json')
    assert mount['target']==str(ROOT/'scratch/run/merged')
    live_mounts=audit.mount_records(ROOT)
    assert live_mounts == {}, 'OWNED_MOUNT_MUST_REMAIN_ABSENT'
    scratch=ROOT/'scratch/run'
    assert not scratch.is_symlink()
    scratch_present=scratch.exists()
    if scratch_present:assert audit.directory_identity(scratch)==mount['scratch_identity']
    if live_mounts:assert scratch_present
    cpu_state=Path(audit.cpu_restore_state());assert cpu_state==ROOT/'scratch/cpu-state'
    journal=cpu_state/'cpu-sysfs.orig';original_bytes=recovery.journal_bytes(journal)
    originals=dict(line.split('\t',1) for line in original_bytes.decode().splitlines())
    assert set(originals)=={f'/sys/devices/system/cpu/cpufreq/policy{i}/{name}' for i in range(16) for name in ('scaling_governor','scaling_max_freq')}
    for path,value in originals.items():
        assert Path(path).resolve(strict=True)==Path(path)
        governor=path.endswith('/scaling_governor')
        assert value==('performance' if governor else '5389000')
        assert Path(path).read_text().strip() in ({'performance'} if governor else {'3800000','5389000'})
    recovery.write('absent-recovery-originals.json',originals)
    recovery.write('absent-recovery-before.json',{'container_absent':True,'normal_shutdown':'UNKNOWN','mount_record':mount,
                   'cpu_current':{p:Path(p).read_text().strip() for p in originals}})
    # Saved CID absence, stable checkpoint and helper quiescence are all checked again by the caller/helper.
    guard.owned(ROOT,LOCK);no_foreign();quiescent()
    if live_mounts:
        audit.node_unmount(STATE)
    assert not audit.mount_records(ROOT)
    recovery.call(['bash','-c','source "$1"; db_fingerprint "$2" "$3"','rpc-absent-recovery',
                  str(REPOSITORY/'scripts/rpc-bench/lib.sh'),'/mnt/sda/nethermind-flat-25490000',str(ROOT/'absent-recovery-db-final.txt')],
                  'absent-recovery-fingerprint.log')
    assert hashlib.sha256((ROOT/'absent-recovery-db-final.txt').read_bytes()).hexdigest()=='c05b88644ecedf04dcf484f8c7cb90ed2291ad08e816b069bc6f6ab0adf086cd'
    quiescent();no_foreign();guard.owned(ROOT,LOCK)
    assert recovery.journal_bytes(journal)==original_bytes
    recovery.call(['bash',str(REPOSITORY/'scripts/rpc-bench/cpu-stabilize.sh'),'restore'],'absent-recovery-cpu-restore.log',
                  environment=dict(os.environ,STATE_DIR=str(cpu_state),CPU_SYSFS='/sys/devices/system/cpu'))
    assert not journal.exists() or journal.stat().st_size==0
    readback={p:Path(p).read_text().strip() for p in originals}
    recovery.write('absent-recovery-readback.json',readback);assert readback==originals
    quiescent();no_foreign();guard.owned(ROOT,LOCK)
    assert audit.owned_cid(STATE,'node',allow_absent=True) is None and not audit.mount_records(ROOT)
    if scratch_present:assert audit.directory_identity(scratch)==mount['scratch_identity']
    else:assert not scratch.exists()
    recovery.write('absent-recovery-result.json',{'status':'RESOURCES_RESTORED_PENDING_INDEPENDENT_AUDIT',
                   'target_run':RUN,'recovery_run':actual_run,'normal_shutdown':'UNKNOWN',
                   'container_state':'ALREADY_ABSENT_BEFORE_RECOVERY','node_mutations':False,'snapshot_unchanged':True,
                   'cpu_values_restored':32,'mounts_remaining':0,
                   'mount_before_recovery':'OWNED_CHECKPOINT_PRESENT' if live_mounts else 'ALREADY_ABSENT',
                   'raw_overlay_retained':scratch_present,'lock_and_hold_retained':True})
    print('RESOURCES_RESTORED_PENDING_INDEPENDENT_AUDIT')


if __name__=='__main__':
    try:main()
    except Exception as error:
        print('ABSENT_RECOVERY_HOLD:'+type(error).__name__,file=sys.stderr)
        raise SystemExit(1)
