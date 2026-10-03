# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
"""Offline numeric projection; never exports raw scheduler text or invokes perf."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import sys

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
import scheduler_collector as collector

MIB = 1024 * 1024
SOURCE_LIMITS = {'started.json': 4 * MIB, 'result.json': 4 * MIB,
                 'decoder-status.json': 4 * MIB, 'decode.json': 4 * MIB,
                 'sched.data': collector.RECORD_LIMIT - 1, 'sched.script.txt': collector.SCRIPT_LIMIT - 1,
                 'perf-record.log': collector.RECORD_FILE_LIMIT, 'perf-script.log': collector.SCRIPT_LIMIT}
COUNTERS = ('lost_records', 'lost_events_known', 'lost_records_unparsed', 'malformed_lines')
EVENT_CODES = {'sched_switch': 0, 'sched_wakeup': 1, 'sched_wakeup_new': 2}
STATE_BITS = {name: 1 << index for index, name in enumerate('RSDTtXZPI')}
COLLECTOR_SHA = hashlib.sha256(Path(collector.__file__).read_bytes()).hexdigest()


class ExportError(RuntimeError):
    pass


def integer(value, minimum=0, maximum=2**63 - 1):
    if type(value) is not int or not minimum <= value <= maximum:
        raise ExportError('INVALID_NUMERIC_METADATA')
    return value


def clock(value):
    result = {key: integer(value[key]) for key in
              ('monotonic_before_ns', 'realtime_ns', 'monotonic_after_ns')}
    if result['monotonic_after_ns'] < result['monotonic_before_ns']:
        raise ExportError('INVALID_CLOCK_BRACKET')
    return result


def validate_private_directory(directory):
    info = directory.lstat()
    if not hasattr(os, 'getuid'):
        raise ExportError('POSIX_PRIVATE_ACCESS_CHECK_REQUIRED')
    if not stat.S_ISDIR(info.st_mode) or info.st_uid != os.getuid() or stat.S_IMODE(info.st_mode) != 0o700:
        raise ExportError('PRIVATE_SOURCE_DIRECTORY_REQUIRED')


def file_identity(path, limit):
    info = path.lstat()
    if not stat.S_ISREG(info.st_mode) or not 0 <= info.st_size <= limit:
        raise ExportError('SOURCE_FILE_TYPE_OR_SIZE_INVALID')
    return (info.st_dev, info.st_ino, info.st_size, info.st_mtime_ns)


def source_hashes(directory):
    identities, hashes = {}, {}
    for name, limit in SOURCE_LIMITS.items():
        path = directory / name
        identities[name] = file_identity(path, limit)
        digest = hashlib.sha256()
        with path.open('rb') as stream:
            for chunk in iter(lambda: stream.read(MIB), b''):
                digest.update(chunk)
        hashes[name] = {'sha256': digest.hexdigest(), 'bytes': identities[name][2]}
    return identities, hashes


def load_json(directory, name):
    value = json.loads((directory / name).read_text(encoding='utf-8'))
    if not isinstance(value, dict):
        raise ExportError('SOURCE_METADATA_OBJECT_REQUIRED')
    return value


def thread_identity(value, tgid):
    tid = integer(value['tid'], 1, 2**31 - 1)
    if integer(value['tgid'], 1, 2**31 - 1) != tgid:
        raise ExportError('THREAD_TARGET_OWNER_MISMATCH')
    nspid = [integer(pid, 1, 2**31 - 1) for pid in value['nspid']]
    if not nspid or nspid[0] != tid:
        raise ExportError('THREAD_NAMESPACE_ID_INVALID')
    return {'tid': tid, 'tgid': tgid, 'start_ticks': integer(value['start_ticks'], 1), 'nspid': nspid}


def observations(started, result):
    initial, final = started['initial_target'], result['final_target']
    pid = integer(initial['leader']['tid'], 1, 2**31 - 1)
    leader = thread_identity(initial['leader'], pid)
    seen = {}
    snapshots = [initial, *result['snapshots'], final]
    for snapshot in snapshots:
        if thread_identity(snapshot['leader'], pid) != leader:
            raise ExportError('TARGET_LEADER_CHANGED')
        bracket = clock(snapshot['clock'])
        if len(snapshot['threads']) > 4096:
            raise ExportError('THREAD_MAP_LIMIT')
        for item in snapshot['threads']:
            item = thread_identity(item, pid)
            key = (item['tid'], item['start_ticks'], tuple(item['nspid']))
            entry = seen.setdefault(key, item | {'first_clock': bracket, 'last_clock': bracket})
            if bracket['monotonic_before_ns'] < entry['first_clock']['monotonic_before_ns']:
                entry['first_clock'] = bracket
            if bracket['monotonic_after_ns'] > entry['last_clock']['monotonic_after_ns']:
                entry['last_clock'] = bracket
        if len(seen) > 65536:
            raise ExportError('OBSERVED_ID_LIMIT')
    generations = {}
    for entry in seen.values():
        generations.setdefault(entry['tid'], []).append(entry)
    eligible, rejected = {}, {}
    for tid, entries in generations.items():
        if len(entries) != 1:
            rejected[tid] = 'observed_reuse'
        elif entries[0]['first_clock']['monotonic_after_ns'] >= entries[0]['last_clock']['monotonic_before_ns']:
            rejected[tid] = 'single_observation_or_no_interval'
        else:
            eligible[tid] = entries[0]
    return leader, eligible, rejected


def state_flags(value):
    preempted = int(value.endswith('+'))
    names = value.rstrip('+').split('|')
    if any(name not in STATE_BITS for name in names):
        raise ExportError('UNSUPPORTED_TARGET_STATE')
    return sum(STATE_BITS[name] for name in set(names)), preempted


def export(directory, output):
    if not callable(getattr(collector, 'parse_event', None)):
        raise ExportError('STRUCTURED_PARSER_REQUIRED')
    directory = Path(directory).absolute()
    validate_private_directory(directory)
    directory = directory.resolve()
    output = Path(output).resolve()
    if output.exists() or output.is_relative_to(directory):
        raise ExportError('NEW_SEPARATE_OUTPUT_DIRECTORY_REQUIRED')
    identities, hashes = source_hashes(directory)
    started, result = load_json(directory, 'started.json'), load_json(directory, 'result.json')
    decoder, decoded = load_json(directory, 'decoder-status.json'), load_json(directory, 'decode.json')
    if result.get('status') != 'RECORDED_REQUIRES_LINUX_VALIDATION' or result.get('replay_completed') is not True:
        raise ExportError('INCOMPLETE_OR_UNKNOWN_CAPTURE')
    record_rc = integer(result['record_returncode'], -128, 255)
    stop = result.get('owned_stop') or {}
    if record_rc != 0 and not (record_rc == -2 and stop.get('alive_before_stop') is True and stop.get('sigint_sent') is True):
        raise ExportError('UNCONFIRMED_RECORD_STOP')
    if decoder.get('status') != 'EXITED' or decoder.get('returncode') != 0 or hashes['perf-script.log']['bytes']:
        raise ExportError('DECODER_NOT_CLEANLY_COMPLETED')
    if decoded.get('status') not in ('REQUIRES_CAPABILITY_AND_WINDOW_REVIEW', 'INVALID'):
        raise ExportError('DECODE_STATUS_UNKNOWN')
    if result['clock_id'] != 'CLOCK_MONOTONIC' or not re.fullmatch(r'[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}', result['boot_id']):
        raise ExportError('CLOCK_IDENTITY_INVALID')
    clocks = {name: clock(result[name]) for name in ('enable_before', 'enable_ack', 'stop_requested', 'stopped')}
    phases = list(clocks.values())
    if any(before['monotonic_after_ns'] > after['monotonic_before_ns'] for before, after in zip(phases, phases[1:])):
        raise ExportError('CLOCK_PHASE_ORDER_INVALID')
    ticks = integer(result['clock_ticks_per_second'], 1, 1000000)
    leader, eligible, rejected = observations(started, result)
    script = directory / 'sched.script.txt'
    with script.open(encoding='utf-8') as stream:
        summary = collector.parse_script(stream)
    global_stats = {key: integer(summary[key]) for key in COUNTERS}
    global_stats['events'] = {name: integer(summary['events'][name]) for name in EVENT_CODES}
    global_stats.update({key: None if summary[key] is None else integer(summary[key]) for key in ('first_ns', 'last_ns')})
    if summary['first_ns'] is not None and not clocks['enable_before']['monotonic_before_ns'] <= summary['first_ns'] <= summary['last_ns'] <= clocks['stopped']['monotonic_after_ns']:
        raise ExportError('TRACE_OUTSIDE_CAPTURE_CLOCK_BOUNDS')
    for key in (*COUNTERS, 'events', 'first_ns', 'last_ns'):
        if decoded[key] != summary[key]:
            raise ExportError('DECODE_GLOBAL_STATS_MISMATCH')
    if decoded['status'] == 'INVALID' and not (summary['lost_records'] or summary['malformed_lines'] or not summary['events']['sched_switch']):
        raise ExportError('UNEXPLAINED_INVALID_DECODE')
    output.mkdir(mode=0o700, parents=True, exist_ok=False)
    data = output / 'target-events.jsonl'
    counts = {key: 0 for key in ('retained_source_events', 'dropped_source_events', 'output_rows', 'unknown_tid_references', 'reused_tid_references', 'single_observation_references', 'outside_observed_interval_references')}
    digest, byte_count = hashlib.sha256(), 0

    def allowed(tid, timestamp):
        if tid in rejected:
            counts['reused_tid_references' if rejected[tid] == 'observed_reuse' else 'single_observation_references'] += 1
            return False
        item = eligible.get(tid)
        if item is None:
            counts['unknown_tid_references'] += 1
            return False
        if not item['first_clock']['monotonic_after_ns'] <= timestamp <= item['last_clock']['monotonic_before_ns']:
            counts['outside_observed_interval_references'] += 1
            return False
        return True

    with script.open(encoding='utf-8') as stream, data.open('xb') as target:
        for line in stream:
            event = collector.parse_event(line)
            if event is None:
                continue
            timestamp = integer(event['timestamp_ns'])
            base = {'timestamp_ns': timestamp, 'cpu': integer(event['cpu'], 0, 65535), 'event': EVENT_CODES[event['event']]}
            rows = []
            if event['event'] == 'sched_switch':
                if allowed(integer(event['prev_pid'], 0, 2**31 - 1), timestamp):
                    flags, preempted = state_flags(event['prev_state'])
                    rows.append(base | {'target_tid': event['prev_pid'], 'direction': 0, 'state_flags': flags, 'preempted': preempted})
                if allowed(integer(event['next_pid'], 0, 2**31 - 1), timestamp):
                    rows.append(base | {'target_tid': event['next_pid'], 'direction': 1})
            elif allowed(integer(event['pid'], 0, 2**31 - 1), timestamp):
                rows.append(base | {'target_tid': event['pid'], 'direction': 2, 'target_cpu': integer(event['target_cpu'], 0, 65535)})
            counts['retained_source_events' if rows else 'dropped_source_events'] += 1
            for row in rows:
                encoded = (json.dumps(row, separators=(',', ':')) + '\n').encode()
                byte_count += len(encoded)
                if byte_count > collector.PROJECTION_LIMIT:
                    raise ExportError('PUBLIC_PROJECTION_SIZE_LIMIT')
                target.write(encoded)
                digest.update(encoded)
                counts['output_rows'] += 1
    for name, identity in identities.items():
        if file_identity(directory / name, SOURCE_LIMITS[name]) != identity:
            raise ExportError('SOURCE_CHANGED_DURING_EXPORT')
    if hashlib.sha256(Path(collector.__file__).read_bytes()).hexdigest() != COLLECTOR_SHA:
        raise ExportError('PARSER_CHANGED_DURING_EXPORT')
    invalid = bool(summary['lost_records'] or summary['malformed_lines'] or not summary['events']['sched_switch'])
    status = 'INVALID_SOURCE_QUALITY' if invalid else ('EMPTY_PROJECTION_REQUIRES_REVIEW' if not counts['output_rows'] else 'PROJECTION_COMPLETE_REQUIRES_CAPABILITY_REVIEW')
    manifest = {
        'schema': 1, 'status': status, 'thread_map_complete': False, 'identity_certified': False,
        'linux_capability_verified': False, 'request_window_verified': False,
        'record_returncode': record_rc, 'decoder_returncode': 0,
        'clock_id': 'CLOCK_MONOTONIC', 'boot_id': result['boot_id'], 'clock_ticks_per_second': ticks, 'clock_brackets': clocks,
        'target_leader': leader, 'eligible_observed_ids': sorted(eligible.values(), key=lambda item: item['tid']),
        'rejected_observed_ids': [{'tid': tid, 'reason': rejected[tid]} for tid in sorted(rejected)],
        'global_stats_before_filtering': global_stats, 'projection_counts': counts,
        'source_files': hashes, 'collector_sha256': COLLECTOR_SHA, 'exporter_sha256': hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        'projection': {'file': 'target-events.jsonl', 'sha256': digest.hexdigest(), 'bytes': byte_count},
        'event_codes': EVENT_CODES, 'direction_codes': {'switch_out': 0, 'switch_in': 1, 'wakeup': 2}, 'printed_state_bits': STATE_BITS,
        'limitations': 'Observed target IDs only; polling cannot rule out unobserved TID reuse/lifetimes. No extrapolation beyond observation brackets. Printed state bits are exporter codes, not native kernel state bits. Global loss counts precede filtering. Raw remains private; hashes do not replace access for independent reconstruction. This artifact is not Linux capability or performance PASS.'
    }
    encoded = (json.dumps(manifest, indent=2) + '\n').encode()
    if len(encoded) > 4 * MIB:
        raise ExportError('PUBLIC_METADATA_SIZE_LIMIT')
    (output / 'projection.json').write_bytes(encoded)
    return manifest


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--input-dir', required=True)
    parser.add_argument('--output-dir', required=True)
    args = parser.parse_args()
    try:
        result = export(args.input_dir, args.output_dir)
    except ExportError as error:
        parser.exit(1, str(error) + '\n')
    except (OSError, ValueError, KeyError, TypeError, collector.CaptureError):
        parser.exit(1, 'SOURCE_OR_EXPORT_VALIDATION_FAILED\n')
    print(json.dumps({'status': result['status'], 'output_rows': result['projection_counts']['output_rows']}))
    return 0 if result['status'] == 'PROJECTION_COMPLETE_REQUIRES_CAPABILITY_REVIEW' else 2


if __name__ == '__main__':
    sys.exit(main())
