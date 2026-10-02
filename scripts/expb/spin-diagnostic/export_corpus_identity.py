"""Export physical JSONL indexes from the pinned EXPB payload/FCU reader contract."""
import argparse
import hashlib
import itertools
import json
import os
from pathlib import Path
import re


def parse_pair(payload_line, fcu_line, index):
    try:
        payload_rpc = json.loads(payload_line.decode('utf-8'))
        fcu_rpc = json.loads(fcu_line.decode('utf-8'))
        if not re.fullmatch(r'engine_newPayloadV[1-4]', payload_rpc['method']):
            raise ValueError('Unexpected newPayload method')
        if not re.fullmatch(r'engine_forkchoiceUpdatedV[1-3]', fcu_rpc['method']):
            raise ValueError('Unexpected FCU method')
        payload = payload_rpc['params'][0]
        head_hash = fcu_rpc['params'][0]['headBlockHash']
        block_hash = payload['blockHash']
        if not isinstance(block_hash, str) or not re.fullmatch(r'0x[0-9a-fA-F]{64}', block_hash):
            raise ValueError('Invalid blockHash')
        if not isinstance(head_hash, str) or head_hash.lower() != block_hash.lower():
            raise ValueError('FCU headBlockHash does not match payload blockHash')
        numbers = {}
        for key in ('blockNumber', 'gasUsed'):
            value = payload[key]
            if not isinstance(value, str) or not re.fullmatch(r'0x[0-9a-fA-F]+', value):
                raise ValueError('Invalid hex quantity ' + key)
            numbers[key] = int(value, 16)
        return {'index': index, 'blockNumber': numbers['blockNumber'], 'blockHash': block_hash.lower(), 'gasUsed': numbers['gasUsed']}
    except (KeyError, IndexError, TypeError, ValueError) as error:
        raise ValueError(f'Invalid payload/FCU pair at physical index {index}: {error}') from error


def stat_identity(stat):
    return (stat.st_dev, stat.st_ino, stat.st_size, stat.st_mtime_ns)


def export(payloads, fcus, output, start, count, expb_sha):
    if start < 0 or count <= 0:
        raise ValueError('start must be >= 0 and count must be > 0')
    if not re.fullmatch(r'[0-9a-f]{40}', expb_sha):
        raise ValueError('expb-sha must be a full lowercase Git commit SHA')
    paths = [Path(payloads).resolve(strict=True), Path(fcus).resolve(strict=True)]
    output = Path(output).resolve()
    if output.exists():
        raise ValueError('Output directory already exists; refusing to overwrite evidence')
    hashes = [hashlib.sha256(), hashlib.sha256()]
    byte_counts = [0, 0]
    rows = []
    stop = start + count
    line_count = 0
    with paths[0].open('rb') as pf, paths[1].open('rb') as ff:
        handles = [pf, ff]
        before = [stat_identity(os.fstat(handle.fileno())) for handle in handles]
        for index, pair in enumerate(itertools.zip_longest(pf, ff)):
            if any(line is None for line in pair):
                raise ValueError('Payload and FCU physical line counts differ')
            for n, line in enumerate(pair):
                hashes[n].update(line)
                byte_counts[n] += len(line)
            # The server consumes physical lines, including warmup; never skip blanks.
            if index < stop:
                row = parse_pair(pair[0], pair[1], index)
                if index >= start:
                    rows.append(row)
            line_count = index + 1
        after = [stat_identity(os.fstat(handle.fileno())) for handle in handles]
        current_paths = [stat_identity(path.stat()) for path in paths]
        if before != after or after != current_paths:
            raise ValueError('Corpus changed during export')
    if len(rows) != count:
        raise ValueError(f'Incomplete selected range: expected {count}, found {len(rows)}')
    if any(byte_counts[n] != before[n][2] for n in range(2)):
        raise ValueError('Corpus byte count does not match stable file size')
    data = ''.join(json.dumps(row, separators=(',', ':')) + '\n' for row in rows).encode('utf-8')
    metadata = {
        'status': 'SOURCE_MAP_COMPLETE', 'index_definition': 'Zero-based physical JSONL line index; no blank-line removal, no guessed block-number offset.',
        'index_start': start, 'index_end_inclusive': stop - 1, 'selected_count': count,
        'warmup_prefix_pairs_validated': start, 'selected_fcu_heads_match_payload_hashes': True,
        'validation_scope': 'Payload/FCU structure verified for warmup prefix and selected range only; all later raw bytes included in corpus SHA256.',
        'source_expb_commit_declared': expb_sha,
        'reader_contract_reference': 'src/expb/payloads/executor/services/payload_server.py:194-212',
        'files': {name: {'source_path': str(paths[n]), 'sha256': hashes[n].hexdigest(), 'byte_count': byte_counts[n], 'physical_line_count': line_count, 'stable_stat_before_after': True} for n, name in enumerate(('payloads', 'fcus'))},
        'map': {'file': 'identity-map.jsonl', 'sha256': hashlib.sha256(data).hexdigest(), 'byte_count': len(data)},
        'first_selected': rows[0], 'last_selected': rows[-1],
        'limitations': 'Source-file identity only. Compare this artifact with preserved request/SSE/client evidence before certifying a historical runtime join; no benchmark was executed.'
    }
    output.mkdir(parents=True, exist_ok=False)
    (output / 'identity-map.jsonl').write_bytes(data)
    (output / 'corpus-identity.json').write_text(json.dumps(metadata, indent=2) + '\n', encoding='utf-8')
    return metadata


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--payloads', required=True)
    parser.add_argument('--fcus', required=True)
    parser.add_argument('--output-dir', required=True)
    parser.add_argument('--start-index', required=True, type=int)
    parser.add_argument('--count', required=True, type=int)
    parser.add_argument('--expb-sha', required=True)
    args = parser.parse_args()
    try:
        result = export(args.payloads, args.fcus, args.output_dir, args.start_index, args.count, args.expb_sha)
    except (OSError, ValueError) as error:
        parser.exit(1, str(error) + '\n')
    print(json.dumps({'status': result['status'], 'selected_count': result['selected_count'], 'map_sha256': result['map']['sha256']}))


if __name__ == '__main__':
    main()
