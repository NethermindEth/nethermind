#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
"""Render separate V8/V9 diagnostic count timelines, not a throughput comparison."""
import argparse
import csv
import json
from pathlib import Path

import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--input-dir', type=Path, default=Path(__file__).resolve().parent)
    parser.add_argument('--out', type=Path, default=Path(__file__).resolve().parent / 'quic-delivery')
    args = parser.parse_args()
    metadata = json.loads((args.input_dir / 'timelines.json').read_text())
    with (args.input_dir / 'timelines.csv').open() as stream:
        rows = list(csv.DictReader(stream))
    plt.rcParams.update({'font.size': 10, 'axes.spines.top': False, 'axes.spines.right': False,
                         'figure.dpi': 160, 'savefig.bbox': 'tight', 'svg.fonttype': 'none', 'svg.hashsalt': 'eip8437-v8-v9-diagnostic'})
    fig, axes = plt.subplots(2, 1, figsize=(7, 9.4), sharex=True)
    colors = {'originated': '#145DA0', 'completed': '#C1492F'}
    names = {'originated': 'Actual offered objects', 'completed': 'Receiver-completed objects'}
    for ax, case in zip(axes, metadata):
        expected_scope = dict(objects=192, transactions_per_object=16, burst=32, interval_ms=1000,
                              rtt_ms=50.0, rate_mbit=100, binding='ethp2p', topology='pair', verify_ms=0, broadcast=False)
        if any(case['provenance']['arguments'].get(key) != value for key, value in expected_scope.items()):
            raise ValueError('Diagnostic input differs from the stated plot scope')
        source = case['provenance']['source_revision']
        selected = [row for row in rows if row['campaign'] == case['campaign']]
        if {row['source_revision'] for row in selected} != {source}:
            raise ValueError('Timeline source identity differs from its metadata')
        inset = ax.inset_axes([.43, .12, .54, .43])
        end = case['controller_goodput_window_seconds']
        for event in colors:
            times = sorted(float(row['seconds']) for row in selected if row['event'] == event)
            expected_count = case['intended_objects'] if event == 'originated' else case['completed_objects']
            if len(times) != expected_count or any(time < 0 or time > end for time in times):
                raise ValueError('Timeline count or time range differs from metadata')
            for axis in (ax, inset):
                axis.step([0, *times, end], [0, *range(1, len(times)+1), len(times)], where='post',
                          color=colors[event], linestyle='--' if event == 'originated' else '-',
                          linewidth=1.8, label=names[event])
        for axis in (ax, inset):
            axis.axvspan(0, case['scheduled_release_seconds'], color='#145DA0', alpha=.06)
            axis.set_ylim(0, 205)
            axis.grid(alpha=.16)
        ax.set(title=f"{case['campaign']} · {source[:10]} · one sample", ylabel='Cumulative objects', xlim=(0, 85))
        ax.set_yticks([0, 64, 128, 192])
        ax.axvline(end, color='#44546a', linestyle=':', linewidth=1)
        last = case['last_completion_seconds']
        ax.plot(last, case['completed_objects'], marker='o', color=colors['completed'], markersize=4)
        ax.text(.035, .82, f"{case['completed_objects']}/192 completed; {case['missing_objects']} never fetched", transform=ax.transAxes,
                fontsize=10, color=colors['completed'], bbox={'facecolor':'white', 'edgecolor':'none', 'alpha':.9})
        ax.text(.035, .88, f"Last delivery: {last:.2f}s", transform=ax.transAxes, fontsize=9, bbox={'facecolor':'white', 'edgecolor':'none', 'alpha':.9})
        ax.text(.97, .64, f"Observation ends: {end:.2f}s", transform=ax.transAxes,
                fontsize=8, ha='right', color='#44546a', bbox={'facecolor':'white', 'edgecolor':'none', 'alpha':.9})
        inset.set(xlim=(0, 16), xticks=[0, 4, 8, 12, 16], yticks=[0, 96, 192],
                  title='First 16 seconds (same events)')
        inset.title.set_fontsize(8)
        inset.tick_params(labelsize=8)
        inset.patch.set_facecolor('#ffffff')
    axes[0].legend(loc='upper right', fontsize=8, framealpha=.95)
    axes[-1].set_xlabel('Seconds from each node’s recorded measured start')
    fig.suptitle('QUIC short-burst diagnostics: delivery and drain', fontsize=13, y=.99)
    fig.subplots_adjust(hspace=.36, top=.93, bottom=.15, left=.12, right=.98)
    fig.text(.025, .025,
             'Separate diagnostic sources, one run each; not the final campaign or steady-state capacity.\n'
             '192 distinct wrappers, 16 transactions each; 32-wrapper bursts each second for 6 seconds (shaded).\n'
             'Pair topology; 50 ms RTT, 100 Mbit/s. Distinct source revisions; no pooled comparison.\n'
             'Controlled verifier and pool admission; real ETH recovery. Warmup excluded. No throughput fit.\n'
             'Full wait retained; inset only enlarges the same count events. Both cohorts remain incomplete.',
             fontsize=8, va='bottom', color='#44546a')
    args.out.parent.mkdir(parents=True, exist_ok=True)
    fig.savefig(str(args.out)+'.png')
    svg = Path(str(args.out)+'.svg')
    fig.savefig(svg, metadata={'Date': None})
    svg.write_text('\n'.join(line.rstrip() for line in svg.read_text().splitlines()) + '\n')
    plt.close(fig)


if __name__ == '__main__':
    main()
