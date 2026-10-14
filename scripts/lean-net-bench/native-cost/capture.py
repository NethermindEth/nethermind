#!/usr/bin/env python3
"""Fresh-process native proof measurements. Requires macOS/Linux os.wait4."""
import argparse
import hashlib
import json
import os
import platform
import subprocess
import time
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument('--exe', type=Path, required=True)
parser.add_argument('--out', type=Path, required=True)
parser.add_argument('--repetitions', type=int, default=3)
parser.add_argument('--include-sphincs64', action='store_true')
args = parser.parse_args()
if args.repetitions < 1 or args.repetitions > 10:
    parser.error('repetitions must be 1..10')
if args.out.exists():
    parser.error('output directory must be new')
args.out.mkdir(parents=True)
exe = args.exe.resolve()
out = args.out.resolve()
environment = dict(os.environ, LEANVM_NUM_THREADS='1')
environment.pop('ZK_ALLOC_POISON', None)
metadata = {
    'binarySha256': hashlib.sha256(exe.read_bytes()).hexdigest(),
    'probeSourceSha256': hashlib.sha256(Path(__file__).with_name('src').joinpath('main.rs').read_bytes()).hexdigest(),
    'host': platform.platform(), 'architecture': platform.machine(),
    'logicalCpuCount': os.cpu_count(), 'workers': 1, 'poison': False,
    'peakRssScope': 'fresh process including fixture creation, proving and verification',
}
(out / 'metadata.json').write_text(json.dumps(metadata, indent=2) + '\n')

def run(name, command, extra=None):
    with (out / (name + '.csv')).open('w') as stdout, (out / (name + '.log')).open('w') as stderr:
        started = time.monotonic()
        process = subprocess.Popen([str(exe), *command], stdout=stdout, stderr=stderr, env=environment | (extra or {}))
        _, status, usage = os.wait4(process.pid, 0)
        process.returncode = os.waitstatus_to_exitcode(status)
        result = dict(metadata, case=name, command=[str(exe), *command], exitCode=process.returncode,
                      wallSeconds=time.monotonic() - started,
                      peakRssBytes=usage.ru_maxrss * (1 if platform.system() == 'Darwin' else 1024),
                      userCpuSeconds=usage.ru_utime, systemCpuSeconds=usage.ru_stime)
        (out / (name + '.json')).write_text(json.dumps(result, indent=2) + '\n')
        print(json.dumps(result), flush=True)
        if process.returncode:
            raise SystemExit(process.returncode)

# The final merge has prepared parents; its timed process never prepares leaves.
run('prepare_parents', ['mixed-prepare', str(out / 'parents')])
for repetition in range(1, args.repetitions + 1):
    suffix = '-r' + str(repetition)
    run('sphincs1' + suffix, ['1', 'raw-binary-tree'])
    run('sphincs16' + suffix, ['16', 'raw-binary-tree'])
    run('generic1' + suffix, ['mixed-prepare', str(out / ('generic1' + suffix))], {'EIP8288_PROBE_RAW': 'generic'})
    run('mixed1_1' + suffix, ['mixed-prepare', str(out / ('mixed1_1' + suffix))], {'EIP8288_PROBE_RAW': 'mixed'})
    run('merge_parents' + suffix, ['mixed-merge', str(out / 'parents')])
if args.include_sphincs64:
    run('sphincs64', ['64', 'raw-binary-tree'])
