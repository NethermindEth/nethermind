"""Allowlisted numerical k6 HTTP observations; no headers, URLs or response bodies."""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import math
from pathlib import Path
import re

METRICS = frozenset('http_req_duration http_req_sending http_req_waiting http_req_receiving http_req_blocked http_req_connecting http_req_tls_handshaking http_reqs http_req_failed checks'.split())
PHASES = frozenset('http_req_duration http_req_sending http_req_waiting http_req_receiving'.split())
CHECKS = frozenset(('check_status_200', 'check_payload_status_valid'))
STAMP = re.compile(r'(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d)(?:\.(\d{1,9}))?Z')


def epoch_ns(value):
    match = STAMP.fullmatch(value)
    if not match:
        raise ValueError('expected UTC RFC3339 timestamp with at most nanosecond precision')
    seconds = int(datetime.strptime(match[1], '%Y-%m-%dT%H:%M:%S').replace(tzinfo=timezone.utc).timestamp())
    return seconds * 1_000_000_000 + int((match[2] or '').ljust(9, '0'))


def project(source, start, count):
    if source.stat().st_size > 64 * 1024 * 1024:
        raise ValueError('request metric file exceeds size bound')
    digest = hashlib.sha256()
    groups = {}
    rows = []
    with source.open('rb') as stream:
        for raw in stream:
            digest.update(raw)
            if len(raw) > 65536:
                raise ValueError('oversized request metric record')
            record = json.loads(raw)
            if record.get('type') != 'Point' or record.get('metric') not in METRICS:
                continue
            data = record['data']
            tags = data.get('tags') or {}
            if 'diagnostic_idx' not in tags:
                continue
            idx_text, warmup = tags['diagnostic_idx'], tags.get('diagnostic_warmup')
            if not re.fullmatch(r'0|[1-9][0-9]*', idx_text) or warmup not in ('0', '1'):
                raise ValueError('invalid diagnostic tags')
            kind = tags.get('kind')
            if kind not in ('newPayload', 'forkchoiceUpdated'):
                raise ValueError('unexpected request kind')
            if record['metric'] != 'checks' and tags.get('status') != '200':
                raise ValueError('request status was not 200')
            idx, metric = int(idx_text), record['metric']
            value = data['value']
            if not isinstance(value, (int, float)) or not math.isfinite(value) or value < 0:
                raise ValueError('invalid metric value')
            if metric == 'http_req_failed' and value != 0:
                raise ValueError('request failed')
            if metric == 'checks':
                metric = 'check_' + tags.get('check', '')
                if metric not in CHECKS or value != 1:
                    raise ValueError('request HTTP/JSON-RPC VALID check missing or failed')
            key = (idx, kind, warmup)
            group = groups.setdefault(key, {})
            if metric in group:
                raise ValueError('duplicate request phase')
            row = {'idx': idx, 'kind': kind, 'warmup': warmup == '1', 'metric': metric,
                   'value': value, 'time_unix_ns': epoch_ns(data['time'])}
            group[metric] = row
            rows.append(row)
            if len(rows) > 100000:
                raise ValueError('metric count limit exceeded')
    expected = {(i, k, '0') for i in range(start, start + count) for k in ('newPayload', 'forkchoiceUpdated')}
    if {key for key in groups if key[2] == '0'} != expected:
        raise ValueError('measured request IDs/kinds differ from the planned corpus')
    warmup_expected = {(i, k, '1') for i in range(start) for k in ('newPayload', 'forkchoiceUpdated')}
    if {key for key in groups if key[2] == '1'} != warmup_expected:
        raise ValueError('warmup request IDs/kinds differ from the planned prefix')
    for key, group in groups.items():
        if key[2] == '1' and not 0 <= key[0] < start:
            raise ValueError('unexpected warmup request index')
        if not PHASES | CHECKS <= group.keys():
            raise ValueError('missing phase or successful VALID check')
        if len({group[name]['time_unix_ns'] for name in PHASES}) != 1:
            raise ValueError('request phase timestamps disagree')
        phase_sum = sum(group[name]['value'] for name in PHASES - {'http_req_duration'})
        if abs(phase_sum - group['http_req_duration']['value']) > 0.00001:
            raise ValueError('duration does not equal sending + waiting + receiving')
    return rows, {'status': 'NUMERIC_REQUESTS_VALIDATED_CLOCK_ATTRIBUTION_PENDING',
                  'source_sha256': digest.hexdigest(), 'measured_requests': len(expected),
                  'points': len(rows), 'start_index': start, 'count': count,
                  'timestamp_semantics': 'Native k6 HTTP sample timestamp; precise end boundary and clock error require capability review.',
                  'warmup_included': True}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('source', type=Path)
    parser.add_argument('destination', type=Path)
    parser.add_argument('--start', type=int, default=3)
    parser.add_argument('--count', type=int, default=1000)
    args = parser.parse_args()
    if args.start < 0 or not 1 <= args.count <= 1000:
        raise ValueError('invalid bounded corpus size')
    rows, summary = project(args.source, args.start, args.count)
    args.destination.mkdir(mode=0o700, parents=False, exist_ok=False)
    with (args.destination / 'requests.jsonl').open('x', encoding='utf-8') as out:
        for row in rows:
            out.write(json.dumps(row, separators=(',', ':')) + '\n')
    (args.destination / 'requests-summary.json').write_text(json.dumps(summary, indent=2) + '\n', encoding='utf-8')


if __name__ == '__main__':
    main()
