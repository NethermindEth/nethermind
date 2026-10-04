#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
import argparse
import json
import pathlib
import subprocess
import time

p = argparse.ArgumentParser(description='Passive Linux Docker resource sampler for the bounded mixed-proof test.')
p.add_argument('--el', action='append', required=True)
p.add_argument('--out', required=True, type=pathlib.Path)
p.add_argument('--floor-gib', type=float, required=True)
a = p.parse_args()
assert len(a.el) == 2 and a.floor_gib >= 6
a.out.mkdir(parents=True, exist_ok=True)

def cpu():
    values = list(map(int, pathlib.Path('/proc/stat').read_text().splitlines()[0].split()[1:9]))
    return sum(values), values[3] + values[4]

previous = cpu()
while True:
    time.sleep(1)
    row = {'capturedEpoch': time.time(), 'hostAvailableFloorBytes': int(a.floor_gib * 1024**3),
           'scope': 'finite mixed functionality; separate from archived spam guard',
           'elMemoryBytes': [], 'elMemoryLimits': [], 'elOomKilled': [], 'elRestarts': []}
    try:
        current = cpu()
        row['hostCpuPercent'] = 100 * (1 - (current[1] - previous[1]) / (current[0] - previous[0]))
        previous = current
        mem = {line.split(':', 1)[0]: int(line.split()[1]) * 1024 for line in pathlib.Path('/proc/meminfo').read_text().splitlines()}
        row['hostAvailableBytes'] = mem['MemAvailable']
        command = ['docker', 'inspect', '--format', '{{json .State}} {{.RestartCount}} {{.HostConfig.Memory}}'] + a.el
        lines = subprocess.check_output(command, timeout=5, text=True).splitlines()
        for line in lines:
            state, tail = json.JSONDecoder().raw_decode(line)
            restarts, limit = map(int, line[tail:].split())
            assert state['Running'] and state['Pid'] > 0 and limit > 0
            cgroups = pathlib.Path(f'/proc/{state["Pid"]}/cgroup').read_text().splitlines()
            assert len(cgroups) == 1 and cgroups[0].startswith('0::/')
            path = pathlib.Path('/sys/fs/cgroup') / cgroups[0][3:].lstrip('/')
            row['elMemoryBytes'].append(int((path / 'memory.current').read_text()))
            row['elMemoryLimits'].append(limit)
            row['elOomKilled'].append(state['OOMKilled'])
            row['elRestarts'].append(restarts)
        assert len(row['elMemoryBytes']) == 2
    except Exception as error:
        row['error'] = str(error)[:256]
    temporary = a.out / 'health.tmp'
    temporary.write_text(json.dumps(row))
    temporary.replace(a.out / 'health.json')
    with (a.out / 'resources.jsonl').open('a') as log:
        log.write(json.dumps(row) + '\n')
