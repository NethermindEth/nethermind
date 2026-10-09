#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Build the diagnostic tool layer for both targets and launch the native tool."""
import argparse
import json
import os
from pathlib import Path
import struct
import subprocess
import tempfile


def run(command, log=None):
    result = subprocess.run(command, capture_output=True, text=True)
    if log:
        log.write_text(result.stdout + result.stderr)
    if result.returncode:
        raise RuntimeError(f'{command}: {result.stderr or result.stdout}')
    return result.stdout.strip()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dockerfile', type=Path, default=Path(__file__).resolve().parents[1] / 'Dockerfile.diag')
    parser.add_argument('--output-dir', type=Path)
    args = parser.parse_args()
    output = args.output_dir or Path(tempfile.mkdtemp(prefix='diag-counters-'))
    output.mkdir(parents=True, exist_ok=True)
    lines = args.dockerfile.read_text().splitlines()
    build_from = next(line for line in lines if line.startswith('FROM ') and line.endswith(' AS build'))
    runtime = next(line.split()[1] for line in lines if line.startswith('FROM ') and not line.endswith(' AS build'))
    blocks = []
    for index, line in enumerate(lines):
        if line.startswith('RUN '):
            block = [line]
            while block[-1].rstrip().endswith('\\'):
                index += 1
                block.append(lines[index])
            blocks.append('\n'.join(block))
    tool_layer = next(block for block in blocks if 'dotnet-counters' in block)
    host = run(['docker', 'info', '--format', '{{.Architecture}}'])
    native = {'x86_64': 'amd64', 'aarch64': 'arm64'}[host]
    context = output / 'context'
    context.mkdir(exist_ok=True)
    (context / 'Dockerfile').write_text(build_from + '\nARG TARGETARCH\n' + tool_layer + '\n')
    results = []
    for target, expected_machine in [('amd64', 62), ('arm64', 183)]:
        directory = output / target
        directory.mkdir(exist_ok=True)
        image = f'neth-diag-counters-smoke:{os.getpid()}-{target}'
        container = None
        try:
            run(['docker', 'build', '--platform', f'linux/{native}', '--build-arg', f'TARGETARCH={target}',
                 '-t', image, str(context)], directory / 'build.log')
            container = run(['docker', 'create', image])
            tools = directory / 'tools'
            tools.mkdir(exist_ok=True)
            run(['docker', 'cp', f'{container}:/root/.dotnet/tools/.', str(tools)])
            apphost = (tools / 'dotnet-counters').read_bytes()
            assert apphost[:6] == b'\x7fELF\x02\x01', 'Expected a 64-bit little-endian ELF apphost'
            machine = struct.unpack_from('<H', apphost, 18)[0]
            assert machine == expected_machine, f'{target}: expected ELF machine {expected_machine}, got {machine}'
            result = {'target': target, 'elf_machine': machine, 'native_launch': False}
            if target == native:
                result['version'] = run(['docker', 'run', '--rm', '--platform', f'linux/{native}',
                                         '-v', f'{tools.resolve()}:/opt/diag-tools:ro',
                                         '--entrypoint', '/opt/diag-tools/dotnet-counters', runtime, '--version'],
                                        directory / 'launch.log')
                result['native_launch'] = True
            results.append(result)
            print(json.dumps(result), flush=True)
        finally:
            if container:
                subprocess.run(['docker', 'rm', container], capture_output=True)
            subprocess.run(['docker', 'image', 'rm', image], capture_output=True)
    (output / 'results.json').write_text(json.dumps(results, indent=2) + '\n')


if __name__ == '__main__':
    main()
