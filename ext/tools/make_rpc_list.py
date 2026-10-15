"""Builds the RPC list, src/Nethermind.IlEvm/PrecompileRpc.txt: the state-override contracts read-only calls send, each
entry with its bytecode, kept where compiled code measured faster than the interpreter on every part of the contract.

The source list has one line per contract, `0x<keccak> uses=<records> size=<bytes> code=0x<hex>`, busiest first, as
`C:/tmp/ethcall/rpc-list.txt` was cut from the 50k eth_call corpus. The measurements are replay-harness logs of the
corpus run with `NETHERMIND_ILEVM_DIAG=1` (the Legacy rules, every part of a compiled contract compiled, timed on both
paths in alternating rounds within one process), whose lines `per code 0x<keccak> size= parts=<interpreted>/<compiled>
ops= us=<interpreted>/<compiled> speedup=` give each code's time per part on either path.

A contract is kept when every log measured it, on enough parts of both paths, at a speedup of at least --min-speedup;
one a log did not measure never ran in its corpus and is left out, as is one that ran slower compiled in any log. Each
kept entry gets `qminv=` (0: the queries' runner runs every visit compiled, from the first call) and its code.

    python -I tools/make_rpc_list.py --source rpc-list.txt --measured a.log b.log --out src/Nethermind.IlEvm/PrecompileRpc.txt
"""
import argparse
import re
import sys

ENTRY = re.compile(r'^(0x[0-9a-fA-F]{64})\s+(.*)$')
MEASURED = re.compile(r'per code (0x[0-9a-f]{64}) size=(\d+) parts=(\d+)/(\d+) ops=(\d+) us=([0-9.]+)/([0-9.]+) speedup=([0-9.]+)')


def read_source(path):
    """The source list's entries in order: (keccak, fields without code, code hex), and its comment lines."""
    entries, comments = [], []
    with open(path, encoding='utf-8') as f:
        for raw in f:
            line = raw.strip()
            if not line:
                continue
            if line.startswith('#'):
                comments.append(line)
                continue
            m = ENTRY.match(line)
            if not m:
                continue
            fields, code = {}, None
            for token in m.group(2).split():
                key, _, value = token.partition('=')
                if key == 'code':
                    code = value[2:] if value.startswith('0x') else value
                elif value:
                    fields[key] = value
            entries.append((m.group(1).lower(), fields, code))
    return entries, comments


def read_measured(path):
    """A log's per-code measurements: keccak -> (interpreted parts, compiled parts, speedup)."""
    measured = {}
    with open(path, encoding='utf-8', errors='replace') as f:
        for line in f:
            m = MEASURED.search(line)
            if m:
                measured[m.group(1)] = (int(m.group(3)), int(m.group(4)), float(m.group(8)))
    return measured


def select(entries, logs, min_speedup, min_parts):
    """The entries kept, each with its slowest measured speedup, and why each other was left out."""
    kept, dropped = [], []
    for keccak, fields, code in entries:
        if code is None:
            dropped.append((keccak, 'no code'))
            continue
        if not logs:
            kept.append((keccak, fields, code, None))
            continue
        speedups = []
        for log in logs:
            m = log.get(keccak)
            if m is None or m[0] < min_parts or m[1] < min_parts:
                speedups = None
                break
            speedups.append(m[2])
        if speedups is None:
            ran = any(keccak in log for log in logs)
            dropped.append((keccak, 'too few parts' if ran else 'never ran'))
        elif min(speedups) < min_speedup:
            dropped.append((keccak, f'speedup {min(speedups):.2f}'))
        else:
            kept.append((keccak, fields, code, min(speedups)))
    return kept, dropped


def write(out, kept, dropped, args, comments):
    total = sum(len(code) // 2 for _, _, code, _ in kept)
    with open(out, 'w', encoding='utf-8', newline='\n') as f:
        f.write('# RPC list: contracts read-only calls send as state-override code, which no code database holds, so each entry carries\n')
        f.write('# its bytecode (code=). The Adaptive rules compile it at startup beside the block list and run its contracts compiled\n')
        f.write('# from the first call: qminv=0 admits every visit on the queries\' runner; use=- keeps block processing off\n')
        f.write('# them, as blocks never run state-override code. One line per contract, busiest first.\n')
        for line in comments:
            f.write('# source: ' + line.lstrip('# ') + '\n')
        if args.measured:
            f.write(f'# Rule: compiled code at least {args.min_speedup}x the interpreter per part in every measurement, '
                    f'on at least {args.min_parts} parts of each path: {", ".join(args.measured)}.\n')
        f.write(f'# Kept {len(kept)} contracts, {total} bytes of code; left out {len(dropped)}: '
                + ', '.join(f'{k[:10]} ({why})' for k, why in dropped) + '.\n')
        for keccak, fields, code, speedup in kept:
            extra = ' '.join(f'{k}={v}' for k, v in fields.items() if k in ('uses', 'size'))
            measured = f' speedup={speedup:.2f}' if speedup is not None else ''
            f.write(f'{keccak} {extra}{measured} use=- qminv={args.qminv} code=0x{code}\n')


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--source', required=True)
    parser.add_argument('--measured', nargs='*', default=[])
    parser.add_argument('--min-speedup', type=float, default=1.05)
    parser.add_argument('--min-parts', type=int, default=100)
    parser.add_argument('--qminv', type=int, default=0)
    parser.add_argument('--out', required=True)
    args = parser.parse_args(argv)
    entries, comments = read_source(args.source)
    logs = [read_measured(path) for path in args.measured]
    for path, log in zip(args.measured, logs):
        if not log:
            sys.exit(f'{path}: no per-code lines; run the harness with NETHERMIND_ILEVM_DIAG=1')
    kept, dropped = select(entries, logs, args.min_speedup, args.min_parts)
    write(args.out, kept, dropped, args, comments)
    print(f'kept {len(kept)} of {len(entries)}; left out: ' + ', '.join(f'{k[:10]} ({why})' for k, why in dropped))


if __name__ == '__main__':
    main()
