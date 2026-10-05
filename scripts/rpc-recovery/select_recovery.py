"""Choose one guarded recovery engine; never fall back after selection."""
import hashlib
import json
import os
from pathlib import Path
import sys
import restore as recovery
import restore_absent as absent


def choose(entries):
    if not entries:
        return 'absent'
    if len(entries) == 1 and entries[0].get('ID') == recovery.CID and entries[0].get('State') == 'running':
        return 'live'
    raise ValueError('UNKNOWN_STOPPED_OR_FOREIGN_CONTAINER_HOLD')


def invoke(entries, live_engine, absent_engine):
    selected = choose(entries)
    return (live_engine if selected == 'live' else absent_engine)()


def main():
    if os.environ.get('RUNNER_NAME') != 'reproducible-benchmarks':
        raise ValueError('EXACT_RUNNER_REQUIRED')
    assert os.geteuid() == 0 and os.environ['GITHUB_RUN_ID'] != recovery.RUN
    os.umask(0o077)
    for module, expected in (
        (recovery, '0508a53793c4f2fd06775f6343a3218f1586780c1c0d8723f19a523f492a1882'),
        (absent, 'd5aeac66c7954138d51e8edfa88355e9fe0b63c0e65b5664eb8076fa8063b21a')):
        assert hashlib.sha256(Path(module.__file__).read_bytes()).hexdigest() == expected
    repository = recovery.REPOSITORY
    for name, expected in json.loads((repository / 'scripts/rpc-capability/native/source-pins.json').read_text()).items():
        assert hashlib.sha256((repository / name).read_bytes()).hexdigest() == expected
    sys.path.insert(0, str(repository / 'scripts/rpc-capability'))
    import guard
    assert hashlib.sha256(Path(guard.__file__).read_bytes()).hexdigest() == '4b54bdb3fcf3bb9d93e112ee56cf004c160d00427d29879c809281b79498ad95'
    actual_run, actual_attempt = os.environ['GITHUB_RUN_ID'], os.environ['GITHUB_RUN_ATTEMPT']
    os.environ.update(GITHUB_RUN_ID=recovery.RUN, GITHUB_RUN_ATTEMPT='1',
                      RPC_PRIVATE_STORAGE_ROOT=str(recovery.ROOT), RPC_PRIVATE_SHARED_SCRATCH_ROOT=str(recovery.ROOT.parent))
    owner = guard.owned(recovery.ROOT, recovery.LOCK)
    assert owner['storage_identity'] == [64512,41881671] and owner['lock_identity'] == [64512,41881670]
    entries = [json.loads(line) for line in recovery.output(
        ['docker', 'ps', '-a', '--no-trunc', '--format', '{{json .}}']).splitlines()]
    selected = choose(entries)
    recovery.write('recovery-selection.json', {'recovery_run':actual_run, 'target_run':recovery.RUN,
                   'selected':selected, 'fallback_permitted':False, 'node_cid':recovery.CID})
    os.environ.update(GITHUB_RUN_ID=actual_run, GITHUB_RUN_ATTEMPT=actual_attempt)
    invoke(entries, recovery.main, absent.main)


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        print('SELECTED_RECOVERY_HOLD:' + type(error).__name__, file=sys.stderr)
        raise SystemExit(1)
