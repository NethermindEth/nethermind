#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Render recorded control-plane decision counts; never synthesize an after result."""

import argparse
import html
import json
from pathlib import Path


def counts(path):
    capture = json.loads(path.read_text())
    rows = capture["results"]

    def row(scenario):
        matches = [item for item in rows if item["scenario"] == scenario]
        if len(matches) != 1:
            raise ValueError(f"Expected one {scenario} row in {path}")
        return matches[0]

    values = [
        row("background-proof-to-production-blockprocessor")["builderAdditionalProofCalls"],
        row("eligible-completed-wrapper-during-new-proof")["eligibleCompletedWrapperSendsWhileBlocked"],
    ]
    if any(type(value) is not int or value < 0 for value in values):
        raise ValueError(f"Invalid count in {path}")
    stale_delivery = row("removed-selection-during-proof")["staleDelivery"]
    if type(stale_delivery) is not bool:
        raise ValueError(f"Invalid stale-delivery flag in {path}")
    values.append(int(stale_delivery))
    return values


def render(before, after):
    titles = [
        ("Extra builder proving calls", "after background work completes", "Fewer calls avoid repeating proof work."),
        ("Eligible completed deliveries", "held proof · five-second event wait", "Delivery observed before proof release."),
        ("Removed selection delivered", "after old proof completes · 0=no, 1=yes", "Zero keeps removed bodies out of gossip."),
    ]
    parts = [
        '<svg xmlns="http://www.w3.org/2000/svg" width="440" height="640" viewBox="0 0 440 640" role="img" aria-labelledby="title desc">',
        '<title id="title">Nethermind aggregation scheduling decision counts</title>',
        '<desc id="desc">Before and after counts from a controlled proof backend and virtual clock. No native timing or throughput is measured.</desc>',
        '<rect width="440" height="640" fill="#f5f7fb"/>',
        '<g font-family="system-ui, sans-serif" fill="#182435">',
        '<text x="24" y="34" font-size="21" font-weight="700">Aggregation scheduling</text>',
        '<text x="24" y="58" font-size="12">Controlled backend · virtual one-second ticks</text>',
    ]
    for index, (title, subtitle, explanation) in enumerate(titles):
        top = 78 + index * 167
        parts.extend([
            f'<rect x="16" y="{top}" width="408" height="155" rx="12" fill="white"/>',
            f'<text x="30" y="{top + 26}" font-size="17" font-weight="650">{html.escape(title)}</text>',
            f'<text x="30" y="{top + 45}" font-size="12" fill="#526277">{html.escape(subtitle)}</text>',
        ])
        scale = max(1, before[index], after[index])
        for position, (label, value, color) in enumerate([
            ("Before", before[index], "#9c4e0a"), ("After", after[index], "#087f6e")
        ]):
            y = top + 65 + position * 30
            width = 230 * value / scale
            parts.extend([
                f'<text x="30" y="{y + 15}" font-size="13">{label}</text>',
                f'<rect x="92" y="{y}" width="230" height="20" rx="3" fill="#edf1f7"/>',
                f'<rect x="92" y="{y}" width="{width:.2f}" height="20" rx="3" fill="{color}"/>',
                f'<text x="339" y="{y + 15}" font-size="16" font-weight="700">{value}</text>',
            ])
        parts.append(f'<text x="30" y="{top + 137}" font-size="12" fill="#526277">{html.escape(explanation)}</text>')
    parts.extend([
        '<text x="24" y="603" font-size="12">Counts from recorded scheduling checks.</text>',
        '<text x="24" y="623" font-size="12">No native proof speed or throughput claim.</text>',
        '</g></svg>',
    ])
    return "\n".join(parts) + "\n"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("before", type=Path)
    parser.add_argument("after", type=Path)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    output = render(counts(args.before), counts(args.after))
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(output)


if __name__ == "__main__":
    main()
