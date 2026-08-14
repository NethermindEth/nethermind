#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
#
# Cross-client parity check of run-jsonbench.sh deep-check captures (JB_DEEP_CHECK=1):
# flags requests where clients disagree on result/error class. Exit 1 on divergence/malformed.

import hashlib
import json
import sys


def classify(resp):
    if not isinstance(resp, dict):
        return ("malformed", None)
    if "_capture_error" in resp:
        return ("malformed", str(resp["_capture_error"])[:60])
    err = resp.get("error")
    if err is not None:
        code = err.get("code") if isinstance(err, dict) else None
        return ("error", code)
    if "result" in resp:
        digest = hashlib.sha256(
            json.dumps(resp["result"], sort_keys=True).encode()
        ).hexdigest()[:12]
        return ("result", digest)
    return ("malformed", None)


def main(argv):
    if len(argv) < 2:
        print("usage: deep-check-compare.py <label>=<file.jsonl> ...", file=sys.stderr)
        return 2

    clients = {}
    for arg in argv[1:]:
        if "=" not in arg:
            print(f"bad arg (expected label=path): {arg}", file=sys.stderr)
            return 2
        label, path = arg.split("=", 1)
        rows = {}
        with open(path) as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                o = json.loads(line)
                rows[o["fp"]] = (o.get("method"), o.get("response"))
        clients[label] = rows

    labels = list(clients)
    # Union of request fingerprints, preserving first-seen order.
    order, seen = [], set()
    for label in labels:
        for fp in clients[label]:
            if fp not in seen:
                seen.add(fp)
                order.append(fp)

    divergent, malformed = [], []
    for fp in order:
        classes, method = {}, None
        for label in labels:
            row = clients[label].get(fp)
            if row is None:
                classes[label] = ("missing", None)
            else:
                method = row[0]
                classes[label] = classify(row[1])
        if any(c[0] == "malformed" for c in classes.values()):
            malformed.append((fp, method, classes))
        if len({c for c in classes.values()}) > 1:
            divergent.append((fp, method, classes))

    print(f"clients:            {labels}")
    print(f"requests compared:  {len(order)}")
    print(f"DIVERGENT (disagree): {len(divergent)}")
    print(f"MALFORMED (any client): {len(malformed)}")
    if divergent:
        print("\n-- divergences (first 60) --")
        for fp, method, classes in divergent[:60]:
            detail = "  ".join(
                f"{lbl}={c[0]}" + (f"#{c[1]}" if c[1] is not None else "")
                for lbl, c in classes.items()
            )
            print(f"  {method} [{fp}]: {detail}")

    return 1 if (divergent or malformed) else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
