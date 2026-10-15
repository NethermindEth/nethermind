#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
"""Extract separate diagnostic timelines from hash-checked raw archive members."""
import argparse
import csv
import hashlib
import json
import tarfile
from pathlib import Path


CASES = (
    ('V8 baseline', 'baseline-v8', 'baseline-v8-ethp2p-tx16-r32'),
    ('V9 recovery pipeline', 'pipeline-diagnostic-v9-tx16', 'pipeline-diagnostic-v9-ethp2p-tx16-r32'),
)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--campaign-root', type=Path, default=Path(__file__).resolve().parent.parent)
    parser.add_argument('--out-dir', type=Path, default=Path(__file__).resolve().parent)
    args = parser.parse_args()
    rows, metadata = [], []
    for label, archive_name, run_name in CASES:
        relative_archive = f'archives/{archive_name}.tar.gz'
        archive_path = args.campaign_root / relative_archive
        manifest = json.loads(archive_path.with_name(archive_name + '.manifest.json').read_text())
        hashes = {entry['path']: entry['sha256'] for entry in manifest['entries']}
        members = [f'{archive_name}/results.jsonl', *(
            f'{archive_name}/runs/{run_name}/result.{index}.json' for index in (0, 1))]
        inputs = {}
        with tarfile.open(archive_path, 'r:gz') as archive:
            for member in members:
                info = archive.getmember(f'{archive_name}/{member}')
                if not info.isfile() or not 0 < info.size <= 16 * 1024 * 1024:
                    raise ValueError(f'Unexpected raw input: {member}')
                with archive.extractfile(info) as stream:
                    data = stream.read()
                if digest(data) != hashes[member]:
                    raise ValueError(f'Raw member hash differs from manifest: {member}')
                inputs[member] = data
        summaries = [json.loads(line) for line in inputs[members[0]].splitlines() if line.strip()]
        summary, = [row for row in summaries if row['binding'] == 'ethp2p' and row['transactions_per_object'] == 16]
        if summary['provenance']['source_revision'] != manifest['source_revision']:
            raise ValueError('Measured source differs from archive manifest')
        source, receiver = (json.loads(inputs[member]) for member in members[1:])
        expected = set(range(summary['warmup'], summary['warmup'] + summary['objects']))
        origins = {int(i): t for i, t in source['originated'].items() if int(i) in expected}
        completed = {int(i): t for i, t in receiver['completed'].items() if int(i) in expected}
        if set(origins) != expected or not set(completed) <= expected:
            raise ValueError('Missing origins or an unexpected completed index')
        if len(completed) != summary['validated_receiver_deliveries'] or len(expected) - len(completed) != summary['missing']:
            raise ValueError('Raw completion counts disagree with summary')
        if summary['events']['1/fetch_started'] != len(completed):
            raise ValueError('Cannot classify missing objects as never fetched')
        for event, node, values in (('originated', source, origins), ('completed', receiver, completed)):
            for index, timestamp in sorted(values.items(), key=lambda item: item[1]):
                rows.append(dict(campaign=label, source_revision=manifest['source_revision'], event=event,
                                 index=index, seconds=(timestamp-node['measured_ns'])/1e9))
        metadata.append(dict(
            campaign=label, provenance=summary['provenance'], raw_archive=relative_archive,
            raw_archive_sha256=digest(archive_path.read_bytes()),
            member_sha256={member: digest(data) for member, data in inputs.items()},
            scheduled_release_seconds=summary['objects']/summary['burst']*summary['interval_ms']/1000,
            controller_goodput_window_seconds=summary['goodput_window_s'],
            intended_objects=len(expected), fetched_objects=summary['events']['1/fetch_started'],
            completed_objects=len(completed), missing_objects=summary['missing'],
            first_completion_seconds=(min(completed.values())-receiver['measured_ns'])/1e9,
            last_completion_seconds=(max(completed.values())-receiver['measured_ns'])/1e9,
            node_measured_start_difference_ns=receiver['measured_ns']-source['measured_ns'],
            incomplete=True, interpretation='One short-burst diagnostic sample; no steady-state capacity inference'))
    args.out_dir.mkdir(parents=True, exist_ok=True)
    with (args.out_dir / 'timelines.csv').open('w', newline='') as stream:
        writer = csv.DictWriter(stream, fieldnames=('campaign', 'source_revision', 'event', 'index', 'seconds'), lineterminator='\n')
        writer.writeheader()
        writer.writerows(rows)
    (args.out_dir / 'timelines.json').write_text(json.dumps(metadata, indent=2)+'\n')


if __name__ == '__main__':
    main()
