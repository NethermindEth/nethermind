"""Tests of tools/make_rpc_list.py: which entries the RPC list keeps, and the lines it writes.

    python -I -m pytest tools/test_make_rpc_list.py
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import make_rpc_list  # noqa: E402

A, B, C, D = ('0x' + c * 64 for c in 'abcd')


def write(tmp_path, name, lines):
    path = tmp_path / name
    path.write_text('\n'.join(lines) + '\n', encoding='utf-8')
    return str(path)


def measured(keccak, interpreted_parts, compiled_parts, speedup):
    return f'per code {keccak} size=10 parts={interpreted_parts}/{compiled_parts} ops=500 us=2.000/1.000 speedup={speedup}'


def source(tmp_path):
    return write(tmp_path, 'source.txt', [
        '# RPC startup list: from the corpus.',
        f'{A} uses=30 size=2 code=0x6000',
        f'{B} uses=20 size=2 code=0x6001',
        f'{C} uses=10 size=2 code=0x6002',
        f'{D} uses=5 size=2',
    ])


def test_keeps_codes_faster_in_every_log_on_enough_parts(tmp_path):
    first = write(tmp_path, 'a.log', ['noise', measured(A[:66], 500, 500, 1.4), measured(B[:66], 500, 500, 0.8), measured(C[:66], 50, 50, 2.0)])
    second = write(tmp_path, 'b.log', [measured(A[:66], 400, 400, 1.2), measured(B[:66], 400, 400, 1.3)])
    out = str(tmp_path / 'out.txt')
    make_rpc_list.main(['--source', source(tmp_path), '--measured', first, second, '--out', out])
    lines = open(out, encoding='utf-8').read().splitlines()
    entries = [line for line in lines if line.startswith('0x')]
    # A kept at its slowest speedup; B slower in one log, C on too few parts and absent from the second, D with no code.
    assert entries == [f'{A} uses=30 size=2 speedup=1.20 use=- qminv=0 code=0x6000']
    header = '\n'.join(line for line in lines if line.startswith('#'))
    assert f'{B[:10]} (speedup 0.80)' in header
    assert f'{C[:10]} (too few parts)' in header
    assert f'{D[:10]} (no code)' in header
    assert '# source: RPC startup list: from the corpus.' in header


def test_a_code_no_log_measured_never_ran(tmp_path):
    log = write(tmp_path, 'a.log', [measured(A[:66], 500, 500, 1.4)])
    out = str(tmp_path / 'out.txt')
    make_rpc_list.main(['--source', source(tmp_path), '--measured', log, '--out', out, '--qminv', '13'])
    text = open(out, encoding='utf-8').read()
    assert f'{B[:10]} (never ran)' in text and f'{C[:10]} (never ran)' in text
    assert f'{A} uses=30 size=2 speedup=1.40 use=- qminv=13 code=0x6000' in text


def test_with_no_measurement_every_entry_with_code_is_kept(tmp_path):
    out = str(tmp_path / 'out.txt')
    make_rpc_list.main(['--source', source(tmp_path), '--out', out])
    entries = [line for line in open(out, encoding='utf-8').read().splitlines() if line.startswith('0x')]
    assert [e.split()[0] for e in entries] == [A, B, C]
    assert all(' use=- qminv=0 code=0x' in e and 'speedup' not in e for e in entries)


def test_a_log_without_per_code_lines_is_refused(tmp_path):
    log = write(tmp_path, 'a.log', ['group | n | ...'])
    try:
        make_rpc_list.main(['--source', source(tmp_path), '--measured', log, '--out', str(tmp_path / 'out.txt')])
    except SystemExit as exit:
        assert 'NETHERMIND_ILEVM_DIAG=1' in str(exit.code)
    else:
        raise AssertionError('a log with no measurement must be refused')
