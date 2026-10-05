"""Recover only the independently attributed resources of RPC run37293502679."""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import select
import stat
import subprocess
import sys

RUN = '37293502679'
CID = '5d2b600363359dcda9ae92ef8a1228246ae014b35350361b90a6ab9544491e39'
ROOT = Path('/mnt/sda/expb-data/rpc-bench-scratch/rpc-private-' + RUN + '-1')
LOCK = ROOT.parent/'rpc-native-capability.lock'
STATE = ROOT/'state/sweep/nativecap'
REPOSITORY = Path(__file__).resolve().parents[2]


def write(name, value):
    with (ROOT/name).open('x') as stream: json.dump(value,stream,indent=2)


def call(arguments, name, timeout=60, environment=None):
    with (ROOT/name).open('xb') as output:
        child=subprocess.Popen(arguments,stdout=output,stderr=subprocess.STDOUT,env=environment,start_new_session=True)
        code=child.wait(timeout=timeout)
        if code: raise ValueError('RECOVERY_COMMAND_FAILED')


def output(arguments, timeout=30):
    child=subprocess.Popen(arguments,stdout=subprocess.PIPE,stderr=subprocess.PIPE,start_new_session=True)
    result,_=child.communicate(timeout=timeout)
    if child.returncode: raise ValueError('RECOVERY_READ_FAILED')
    return result


def journal_bytes(path):
    assert path.resolve(strict=True)==path
    before=path.lstat()
    assert stat.S_ISREG(before.st_mode) and before.st_nlink==1
    expected=(64512,41880642,33152,1,2156,1791194305487325216,1791194305487325216)
    def identity(info):return(info.st_dev,info.st_ino,info.st_mode,info.st_nlink,info.st_size,info.st_mtime_ns,info.st_ctime_ns)
    assert identity(before)==expected
    with os.fdopen(os.open(path,os.O_RDONLY|os.O_NOFOLLOW),'rb') as source:
        assert identity(os.fstat(source.fileno()))==expected
        value=source.read(4096)
        assert identity(os.fstat(source.fileno()))==expected
    assert identity(path.lstat())==expected and value.endswith(b'\n')
    assert hashlib.sha256(value).hexdigest()=='770d5d811e190f666113dbe6ec8090e815a44739ce71f5d5f7eaddf27ca8a437'
    return value


def scan_helpers(allow_node):
    tokens=[str(ROOT).encode(),('rpc-capability-operator-'+RUN+'-1').encode(),
            ('rpc-capability-harness-'+RUN+'-1').encode(),('GITHUB_RUN_ID='+RUN+'\0').encode()]
    matches=[]
    for proc in Path('/proc').iterdir():
        if not proc.name.isdecimal() or int(proc.name)==os.getpid(): continue
        try:
            command=(proc/'cmdline').read_bytes()
            environment=(proc/'environ').read_bytes()
            cgroup=(proc/'cgroup').read_bytes()
            # Kernel threads have no userspace cwd/file descriptors.
            references=[command,environment]
            if command:
                references.append(os.readlink(proc/'cwd').encode())
                for fd in (proc/'fd').iterdir():
                    try: references.append(os.readlink(fd).encode())
                    except FileNotFoundError: pass
            if any(token in entry for token in tokens for entry in references):
                if not (allow_node and CID.encode() in cgroup):
                    matches.append({'pid':int(proc.name),'stat':(proc/'stat').read_text(),'command':command.decode(errors='replace')})
        except (FileNotFoundError,ProcessLookupError): continue
    if matches:
        write('recovery-unclosed-helpers.json',matches)
        raise ValueError('OWNED_HELPERS_NOT_QUIESCENT')


def main():
    os.umask(0o077)
    assert os.geteuid()==0 and os.environ['GITHUB_RUN_ID']!=RUN
    pins=json.loads((REPOSITORY/'scripts/rpc-capability/native/source-pins.json').read_text())
    for relative,digest in pins.items():
        assert hashlib.sha256((REPOSITORY/relative).read_bytes()).hexdigest()==digest
    sys.path.insert(0,str(REPOSITORY/'scripts/rpc-capability'))
    import guard
    new_run,new_attempt=os.environ['GITHUB_RUN_ID'],os.environ['GITHUB_RUN_ATTEMPT']
    os.environ.update(GITHUB_RUN_ID=RUN,GITHUB_RUN_ATTEMPT='1',RPC_PRIVATE_STORAGE_ROOT=str(ROOT),
                      RPC_PRIVATE_SHARED_SCRATCH_ROOT=str(ROOT.parent))
    guard.owned(ROOT,LOCK)
    spec=importlib.util.spec_from_file_location('private_audit',REPOSITORY/'scripts/rpc-bench/private_audit.py')
    audit=importlib.util.module_from_spec(spec);spec.loader.exec_module(audit)
    def audited_output(command,timeout=20):
        value=output(command,timeout)
        if len(value)>1024*1024:raise ValueError('PROVENANCE_OUTPUT_SIZE')
        return value.decode('utf-8',errors='strict').strip()
    audit.command_output=audited_output
    audit.context();audit.storage_context()
    failure=guard.read(ROOT/'native-capability.json')
    assert failure['outcomes']=={'preflight':0,'prepare':0,'cpu-attempt':0,'cpu-apply':0,'start':1}
    assert failure['ownership']=='UNKNOWN_HOLD_NO_CLEANUP_OR_NEXT_DISPATCH'
    assert audit.owned_cid(STATE,'node')==CID
    inspect=json.loads(output(['docker','inspect',CID]))[0]
    assert inspect['State']['Running'] and inspect['RestartCount']==0
    assert any(m['Source']==str(ROOT/'scratch/run/merged') and m['Destination']=='/execution-data' and m['RW'] for m in inspect['Mounts'])
    def no_foreign():
        entries=[json.loads(line) for line in output(['docker','ps','-a','--no-trunc','--format','{{json .}}']).splitlines()]
        assert not any(x['ID']!=CID and (x['Names'].startswith(('rpcbench-','nethermind-rpcbench','ethcallchaos-bench','jsonbench-','expb')) or 'expb=' in x['Labels']) for x in entries)
        assert not output(['docker','ps','-aq','--filter','label=expb']).strip()
    no_foreign()
    mount=guard.read(STATE/'node-mount.json')
    assert audit.mount_records(ROOT)=={mount['target']:mount['record']}
    assert audit.directory_identity(ROOT/'scratch/run')==mount['scratch_identity']
    cpu_state=Path(audit.cpu_restore_state())
    assert cpu_state==ROOT/'scratch/cpu-state'
    journal=cpu_state/'cpu-sysfs.orig'
    originals={}
    recorded_journal=journal_bytes(journal)
    for line in recorded_journal.decode().splitlines():
        path,value=line.split('\t',1)
        match=re.fullmatch(r'/sys/devices/system/cpu/cpufreq/policy(\d+)/(scaling_governor|scaling_max_freq)',path)
        assert match and path not in originals and Path(path).resolve(strict=True)==Path(path)
        assert value==('performance' if match[2]=='scaling_governor' else '5389000')
        assert Path(path).read_text().strip()==('performance' if match[2]=='scaling_governor' else '3800000')
        originals[path]=value
    assert len(originals)==32
    assert set(originals)=={f'/sys/devices/system/cpu/cpufreq/policy{i}/{name}' for i in range(16) for name in ('scaling_governor','scaling_max_freq')}
    write('recovery-cpu-originals.json',originals)
    write('recovery-container-before.json',inspect)
    scan_helpers(True)
    descriptor=os.pidfd_open(inspect['State']['Pid'])
    try:
        assert audit.owned_cid(STATE,'node')==CID
        call(['docker','kill','--signal','SIGINT',CID],'recovery-stop.log',timeout=15)
        poller=select.poll();poller.register(descriptor,select.POLLIN)
        assert poller.poll(60000)
    finally: os.close(descriptor)
    stopped=json.loads(output(['docker','inspect',CID]))[0]
    write('recovery-container-stopped.json',stopped)
    assert not stopped['State']['Running'] and stopped['State']['ExitCode']==0 and not stopped['State']['OOMKilled']
    call(['docker','logs',CID],'recovery-node.log')
    assert 'Nethermind is shut down' in (ROOT/'recovery-node.log').read_text()
    scan_helpers(False);no_foreign()
    assert audit.owned_cid(STATE,'node')==CID
    # Preserve anonymous volumes and the entire owned overlay as forensic evidence.
    call(['docker','rm',CID],'recovery-remove-container.log')
    volumes=[m['Name'] for m in inspect['Mounts'] if m['Type']=='volume']
    assert len(volumes)==3
    for volume in volumes:
        output(['docker','volume','inspect',volume])
    audit.node_unmount(STATE)
    assert not audit.mount_records(ROOT)
    call(['bash','-c','source "$1"; db_fingerprint "$2" "$3"','rpc-recovery',
          str(REPOSITORY/'scripts/rpc-bench/lib.sh'),'/mnt/sda/nethermind-flat-25490000',str(ROOT/'recovery-db-final.txt')],
          'recovery-fingerprint.log')
    assert hashlib.sha256((ROOT/'recovery-db-final.txt').read_bytes()).hexdigest()=='c05b88644ecedf04dcf484f8c7cb90ed2291ad08e816b069bc6f6ab0adf086cd'
    scan_helpers(False);no_foreign();guard.owned(ROOT,LOCK)
    assert journal_bytes(journal)==recorded_journal
    call(['bash',str(REPOSITORY/'scripts/rpc-bench/cpu-stabilize.sh'),'restore'],'recovery-cpu-restore.log',
          environment=dict(os.environ,STATE_DIR=str(cpu_state),CPU_SYSFS='/sys/devices/system/cpu'))
    assert not journal.exists() or journal.stat().st_size==0
    readback={path:Path(path).read_text().strip() for path in originals}
    write('recovery-cpu-readback.json',readback)
    assert readback==originals
    assert not audit.mount_records(ROOT) and audit.owned_cid(STATE,'node',allow_absent=True) is None
    scan_helpers(False);no_foreign();guard.owned(ROOT,LOCK)
    write('recovery-result.json',{'status':'OWNED_RESOURCES_RESTORED_PENDING_INDEPENDENT_AUDIT',
          'target_run':RUN,'recovery_run':new_run,'attempt':new_attempt,'normal_shutdown':True,
          'snapshot_unchanged':True,'cpu_values_restored':len(originals),'node_removed':True,
          'mounts_remaining':0,'raw_overlay_and_volumes_retained':True,'retained_volumes':volumes,'lock_and_hold_retained':True})
    print('OWNED_RESOURCES_RESTORED_PENDING_INDEPENDENT_AUDIT')


if __name__=='__main__':
    try:main()
    except Exception as error:
        print('OWNED_RECOVERY_HOLD:'+type(error).__name__,file=sys.stderr)
        raise SystemExit(1)
