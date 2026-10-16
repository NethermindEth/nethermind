"""Tests of make_list.py on synthetic Stats dumps: python -I -m pytest tools/test_make_list.py"""
import math
import os
import random
import re
import sys

import pytest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import make_list  # noqa: E402

PLUGIN_LIST = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "src", "Nethermind.IlEvm", "Precompile.txt")
LOGGER = "2026-10-14 12:00:00.0000|INFO|IlEvm|"


def h(i):
    return "0x" + f"{i:08x}" * 8


def contract_line(prefix, rank, code_hash, size, entries, blocks, rows):
    """rows: {(path, kind): {bucket: (parts, ops, ns)}}, written as the plugin's Stats dump writes a contract."""
    tokens = []
    for path in "ICH":
        for kind in "MO":
            r = rows.get((path, kind))
            if r:
                tokens.append(f"{path}.{kind}={sum(x[0] for x in r.values())}/{sum(x[1] for x in r.values())}/{sum(x[2] for x in r.values())}")
    for path in "ICH":
        for kind in "MO":
            r = rows.get((path, kind))
            if r:
                tokens.append(f"h{path}.{kind}=" + ",".join(f"{b}:{p}/{o}/{n}/{n // max(1, p)}" for b, (p, o, n) in sorted(r.items())))
    return f"{LOGGER}{prefix} {rank} {code_hash} size={size} entries={entries} blocks={blocks} shipped=0 " + " ".join(tokens)


def dump_lines(contracts, blocks, tag=None):
    """contracts: [(hash, size, entries, blocks, rows)]: a header with the totals, the contract lines, the end line."""
    prefix = "IL-EVM stats" + (f"@{tag}" if tag else "")
    totals = {}
    for _, _, _, _, rows in contracts:
        for key, r in rows.items():
            t = totals.setdefault(key, [0, 0, 0])
            for p, o, n in r.values():
                t[0] += p
                t[1] += o
                t[2] += n
    head = " ".join(f"{path}.{kind}=" + "/".join(str(x) for x in totals.get((path, kind), (0, 0, 0))) for path in "ICH" for kind in "MO")
    lines = [f"{LOGGER}{prefix}: {len(contracts)} contracts, {blocks} blocks, totals {head} gc=0/0 runtime-compiled=0 handoffs=0/0/0 tables=1/1 heapMiB=1"]
    for rank, (code_hash, size, entries, cblocks, rows) in enumerate(contracts, 1):
        lines.append(contract_line(prefix, rank, code_hash, size, entries, cblocks, rows))
    lines.append(f"{LOGGER}{prefix} end")
    return lines


def write(path, *dumps):
    with open(path, "w", encoding="utf-8") as f:
        f.write("some other log line\n")
        for d in dumps:
            f.write("\n".join(d) + "\n")
    return str(path)


def bucket(parts, ops_per_part, ns_per_op):
    ops = parts * ops_per_part
    return parts, ops, int(round(ops * ns_per_op))


def run(tmp_path, a_logs, b_logs, *extra):
    out, rep = tmp_path / "list.txt", tmp_path / "report.md"
    args = ["--a", *a_logs, "--b", *b_logs, "--out", str(out), "--report", str(rep), "--old", PLUGIN_LIST, *extra]
    assert make_list.main(args) == 0
    lines = out.read_text(encoding="utf-8").splitlines()
    entries = {}
    order = []
    for line in lines:
        if line.startswith("#"):
            continue
        tokens = line.split()
        fields = dict(t.split("=", 1) for t in tokens[1:])
        entries[tokens[0]] = fields
        order.append(tokens[0])
    calib = next(line for line in lines if line.startswith("# calib"))
    tokens = dict(t.split("=", 1) for t in calib[len("# calib"):].split())
    return entries, order, tokens, lines, rep.read_text(encoding="utf-8")


# The plugin's Stats tests' own dump, as the log shows it: a snapshot, then the shutdown dump.
REAL = """IL-EVM stats@2500: 2 contracts, 1 blocks, totals I.M=1/16/4300 I.O=0/0/0 C.M=2/10/2500 C.O=0/0/0 H.M=0/0/0 H.O=0/0/0 sI.M=0/0/0 sI.O=0/0/0 sC.M=0/0/0 sC.O=0/0/0 sH.M=0/0/0 sH.O=0/0/0 hI.M=4:1/16/4300/4300 hC.M=1:1/2/300/300,3:1/8/2200/2200 gc=0/0 runtime-compiled=28831 handoffs=0/0/0 tables=2/1 heapMiB=86
IL-EVM stats@2500 1 0xa9937887463a2c334d87edc6eb5fc0429626a15bc4d4bddcbe9f0c8914884c1a size=24 entries=1 blocks=1 shipped=0 I.M=1/16/4300 hI.M=4:1/16/4300/4300
IL-EVM stats@2500 2 0x481e24e9af51550deef98f5277c4102e83ab0271542052c0230146d7cb20dde9 size=35 entries=1 blocks=1 shipped=1 C.M=2/10/2500 hC.M=1:1/2/300/300,3:1/8/2200/2200
IL-EVM stats@2500 end
IL-EVM stats: 3 contracts, 2 blocks, totals I.M=2/32/9500 I.O=1/16/5300 C.M=4/20/7400 C.O=2/10/4300 H.M=1/15/1400 H.O=0/0/0 sI.M=0/0/0 sI.O=0/0/0 sC.M=1/8/4400 sC.O=1/8/4200 sH.M=0/0/0 sH.O=0/0/0 hI.M=4:2/32/9500/6100 hI.O=4:1/16/5300/5300 hC.M=1:2/4/200/100,3:2/16/7200/4400 hC.O=1:1/2/100/100,3:1/8/4200/4200 hH.M=3:1/15/1400/1400 gc=0/0 runtime-compiled=28832 handoffs=0/0/0 tables=5/2 heapMiB=95
IL-EVM stats 2 0x481e24e9af51550deef98f5277c4102e83ab0271542052c0230146d7cb20dde9 size=35 entries=3 blocks=2 shipped=1 C.M=4/20/25500 C.O=2/10/18800 sC.M=2/16/25200 sC.O=1/8/18600 hC.M=1:2/4/300/200,3:2/16/25200/17800 hC.O=1:1/2/200/200,3:1/8/18600/18600
IL-EVM stats end"""


def test_real_format_lines_parse():
    dumps = make_list.parse_log("real.log", [LOGGER + line for line in REAL.splitlines()])
    assert [d.tag for d in dumps] == [2500, None]
    snap, final = dumps
    assert snap.complete and final.complete
    assert snap.blocks == 1 and final.blocks == 2
    assert snap.totals[("C", "M")] == (2, 10, 2500)
    c = final.contracts["0x481e24e9af51550deef98f5277c4102e83ab0271542052c0230146d7cb20dde9"]
    assert (c.size, c.entries, c.blocks, c.shipped) == (35, 3, 2, True)
    assert c.tot[("C", "O")] == (2, 10, 18800)
    assert c.bucket("C", "M", 3) == (2, 16, 25200)
    assert c.bucket("C", "M", 1) == (2, 4, 300)
    assert c.compiled()
    # The slow parts' fields are not totals.
    assert ("sC", "M") not in c.tot and c.tot[("C", "M")] == (4, 20, 25500)
    assert not snap.contracts["0xa9937887463a2c334d87edc6eb5fc0429626a15bc4d4bddcbe9f0c8914884c1a"].compiled()


def test_old_list_parses():
    hashes = make_list.parse_list(PLUGIN_LIST)
    assert len(hashes) == 300
    assert hashes[0] == "0x785f1014552b7ce7d5fb7d0c970ca60edee94fd00425d7ca21609acac7ce1293"
    assert all(re.fullmatch(r"0x[0-9a-f]{64}", x) for x in hashes)


def test_arm_a_with_compiled_parts_is_rejected(tmp_path):
    rows = {("I", "M"): {5: bucket(100, 40, 10)}, ("C", "M"): {5: bucket(10, 40, 5)}}
    a = write(tmp_path / "a.log", dump_lines([(h(1), 100, 100, 100, rows)], 1000))
    b = write(tmp_path / "b.log", dump_lines([(h(1), 100, 100, 100, rows)], 1000))
    with pytest.raises(SystemExit, match="arm A"):
        make_list.main(["--a", a, "--b", b, "--out", str(tmp_path / "x.txt")])


def ranking_arms(tmp_path):
    n = 1000
    a, b = [], []
    # 1: a heavy loop that gains 5 ns per opcode on block processing.
    a.append((h(1), 2000, 5000, 900, {("I", "M"): {10: bucket(5000, 1500, 10)}}))
    b.append((h(1), 2000, 5000, 900, {("C", "M"): {10: bucket(5000, 1500, 5)}}))
    # 2: light, frequent straight-line code that loses 2 ns per opcode.
    a.append((h(2), 5000, 100000, 1000, {("I", "M"): {3: bucket(100000, 10, 10)}}))
    b.append((h(2), 5000, 100000, 1000, {("C", "M"): {3: bucket(100000, 10, 12)}}))
    # 3: gains only on the other threads.
    a.append((h(3), 1500, 4000, 800, {("I", "M"): {5: bucket(2000, 40, 10)}, ("I", "O"): {9: bucket(2000, 600, 10)}}))
    b.append((h(3), 1500, 4000, 800, {("C", "M"): {5: bucket(2000, 40, 10)}, ("C", "O"): {9: bucket(2000, 600, 4)}}))
    # 4: a big gain in too few blocks.
    a.append((h(4), 1000, 3000, 10, {("I", "M"): {10: bucket(3000, 1500, 10)}}))
    b.append((h(4), 1000, 3000, 10, {("C", "M"): {10: bucket(3000, 1500, 5)}}))
    # 5: short parts lose, long ones gain: admitted from bucket 5 on.
    a.append((h(5), 3000, 2000, 700, {("I", "M"): {4: bucket(5000, 20, 10), 12: bucket(1000, 5000, 10)}}))
    b.append((h(5), 3000, 2000, 700, {("C", "M"): {4: bucket(5000, 20, 15), 12: bucket(1000, 5000, 9)}}))
    # 6: uncompiled code that runs slower in arm B: the footprint's charge.
    a.append((h(6), 8000, 50000, 1000, {("I", "M"): {6: bucket(50000, 100, 10)}}))
    b.append((h(6), 8000, 50000, 1000, {("I", "M"): {6: bucket(50000, 100, 10.5)}}))
    # 7: a modest gain on a big code, past the bytes cap once the others are in.
    a.append((h(7), 4_190_000, 4000, 600, {("I", "M"): {9: bucket(4000, 700, 10)}}))
    b.append((h(7), 4_190_000, 4000, 600, {("C", "M"): {9: bucket(4000, 700, 8)}}))
    # 8: a gain within the noise.
    a.append((h(8), 500, 60, 500, {("I", "M"): {7: bucket(30, 200, 10)}}))
    b.append((h(8), 500, 60, 500, {("C", "M"): {7: bucket(30, 200, 9.9)}}))
    return write(tmp_path / "A-12345678.log", dump_lines(a, n)), write(tmp_path / "B-23456789.log", dump_lines(b, n))


def test_ranking_sigma_use_minv_and_caps(tmp_path):
    a, b = ranking_arms(tmp_path)
    entries, order, calib, lines, report = run(tmp_path, [a], [b], "--rho", "0.25")
    # The footprint's charge: contract 6's 2,500 ns per block over the candidates' MiB.
    candidates_bytes = 2000 + 5000 + 1500 + 1000 + 3000 + 4_190_000 + 500
    lam = 0.5 * 50000 * 100 / 1000 / (candidates_bytes * 52.7 / (1 << 20))
    assert float(calib["lambda"]) == pytest.approx(lam, rel=1e-3)
    assert float(calib["rho"]) == 0.25
    # Measured savings, ns per block: 1 = 5 * 7.5M / 1000.
    s1 = 5 * 5000 * 1500 / 1000
    assert float(entries[h(1)]["sM"]) == pytest.approx(s1, abs=1)
    sigma1 = math.sqrt(0.25 * (100 / 5000 + 25 / 5000) * (5000 * 1500 / 1000) ** 2)
    assert float(entries[h(1)]["sigma"]) == pytest.approx(sigma1, abs=1)
    assert float(entries[h(1)]["score"]) == pytest.approx(s1 - lam * 2000 * 52.7 / (1 << 20), abs=1)
    # Light frequent code that loses, a gain in 10 blocks, a gain within the noise, and uncompiled code: none listed.
    for left_out in (h(2), h(4), h(6), h(8)):
        assert left_out not in entries
    # The bytes cap: 7's 4.19 MB do not fit beside the rest.
    assert h(7) not in entries
    # Score order; use and minv.
    assert order == [h(1), h(5), h(3)]
    assert entries[h(1)]["use"] == "m" and entries[h(1)]["L"] == "mixed"
    assert entries[h(3)]["use"] == "w"
    assert entries[h(5)]["minv"] == "5"
    assert all(e["src"] == "ab" for e in entries.values())
    assert "## XEN" in report and "Against the old list" in report
    # The entry count cap keeps the densest by score per byte.
    entries, order, _, _, _ = run(tmp_path, [a], [b], "--rho", "0.25", "--max-entries", "1")
    assert order == [h(1)]


def test_lines_read_as_the_plugin_reads_them(tmp_path):
    a, b = ranking_arms(tmp_path)
    _, _, calib, lines, _ = run(tmp_path, [a], [b])
    known = {"score", "sM", "sO", "sigma", "size", "n", "w", "f", "L", "use", "minv", "src", "ops", "frames"}
    for line in lines:
        if line.startswith("#"):
            continue
        tokens = line.split()
        assert re.fullmatch(r"0x[0-9a-f]{64}", tokens[0])
        for t in tokens[1:]:
            name, value = t.split("=", 1)
            assert name in known
            if name in ("size", "minv", "ops", "frames"):
                assert value.isdigit()
            elif name in ("score", "sM", "sO", "sigma", "n", "w", "f"):
                float(value)
        assert dict(t.split("=", 1) for t in tokens[1:])["L"] in ("line", "mixed", "loop")
    calib_lines = [line for line in lines if line.startswith("# calib")]
    assert len(calib_lines) == 1 and calib_lines[0].startswith("# calib ")
    for value in calib.values():
        float(value)


def test_rho_from_the_processing_difference(tmp_path):
    a, b = ranking_arms(tmp_path)
    arm_a = make_list.Arm("A", [a], 1, None)
    arm_b = make_list.Arm("B", [b], 1, None)
    measured = make_list.measure(arm_a, arm_b)
    lam = make_list.derive_lambda(measured)[0]
    sm = sum(m.s["M"] for m in measured.values() if m.candidate)
    so = sum(m.s["O"] for m in measured.values() if m.candidate)
    footprint = lam * sum(make_list.footprint_mib(m.size) for m in measured.values() if m.candidate)
    # Processing saved what the main thread saved, less the footprint's cost, plus half what the others saved.
    dproc = (sm - footprint + 0.5 * so) / 1e6
    rho, source = make_list.derive_rho("auto", dproc, measured, lam)
    assert rho == pytest.approx(0.5, abs=1e-9) and "dproc" in source
    assert make_list.derive_rho("auto", None, measured, lam)[0] == 0.25
    assert make_list.derive_rho("auto", 1e9, measured, lam)[0] == 1.0


# The fit: candidates whose savings follow the model exactly, with a little noise, over three shapes and a spread of reuse.
TRUE = {"a": {"line": 0.3, "mixed": 0.7, "loop": 1.2}, "g": 30.0, "b": 2.0, "d0": 32, "U": 300}


def model_arms(tmp_path, noise=0.002, scramble=False, seed=7):
    rnd = random.Random(seed)
    n = 1000
    a, b = [], []
    specs = []
    for i in range(48):
        shape = ("line", "mixed", "loop")[i % 3]
        size = rnd.choice((2000, 6000, 12000))
        parts = rnd.choice((200, 600, 2000, 6000, 20000))
        specs.append((i + 1, shape, size, parts))
    # Each contract has three buckets of its parts per kind; the non-candidate adds 5,000 parts of kind M.
    totals = {k: 3 * sum(p for _, _, _, p in specs) + (5000 if k == "M" else 0) for k in "MO"}
    for i, shape, size, parts in specs:
        rows_a, rows_b = {}, {}
        for kind, scale in (("M", 1.0), ("O", 0.8)):
            d = totals[kind] / (3 * parts)
            cold = d / (d + TRUE["d0"])
            for bkt, m in ((6, 90), (9, 700), (11, 2500)):
                y = TRUE["a"][shape] * scale * m - TRUE["g"] - TRUE["b"] * cold * min(m, TRUE["U"])
                if scramble:
                    y = rnd.uniform(-1, 1) * abs(y) * 2
                ua = 10.0
                ub = ua - y / m
                ub *= 1 + rnd.gauss(0, noise)
                rows_a.setdefault(("I", kind), {})[bkt] = bucket(parts, m, ua)
                rows_b.setdefault(("C", kind), {})[bkt] = bucket(parts, m, ub)
        # Entries such that the frames' opcodes per byte give the shape: line under 0.1, loop from 1.
        per_frame = {"line": 0.05, "mixed": 0.5, "loop": 3.0}[shape] * size
        ops_i = sum(r[1] for rows in rows_a.values() for r in rows.values())
        frames = max(1, int(ops_i / per_frame))
        a.append((h(i), size, frames, 800, rows_a))
        b.append((h(i), size, frames, 800, rows_b))
    # A contract no run compiled, which the model expects to gain.
    rows = {("I", "M"): {11: bucket(5000, 2500, 10.0)}}
    a.append((h(999), 3000, 5000 * 2500 // 9000, 900, rows))
    return write(tmp_path / "A.log", dump_lines(a, n)), write(tmp_path / "B.log", dump_lines(b, n))


def test_fit_recovers_the_constants_and_lists_modelled_code(tmp_path):
    a, b = model_arms(tmp_path)
    entries, order, calib, lines, report = run(tmp_path, [a], [b], "--rho", "0.25", "--max-entries", "300")
    fit_line = next(line for line in lines if line.startswith("# Fit:"))
    assert "accepted" in fit_line, fit_line
    assert int(calib["d0"]) == TRUE["d0"] and int(calib["U"]) == TRUE["U"]
    for shape in ("line", "mixed", "loop"):
        assert float(calib[f"a.{shape}.M"]) == pytest.approx(TRUE["a"][shape], rel=0.05)
        assert float(calib[f"a.{shape}.O"]) == pytest.approx(TRUE["a"][shape] * 0.8, rel=0.05)
    assert float(calib["g.M"]) == pytest.approx(TRUE["g"], rel=0.2)
    assert float(calib["b.M"]) == pytest.approx(TRUE["b"], rel=0.1)
    switch = (float(calib["g.M"]) + float(calib["b.M"]) * TRUE["U"]) / float(calib["a.mixed.M"])
    assert int(calib["gate_block_switch_ops"]) == pytest.approx(switch, abs=1)
    assert int(calib["gate_block_visit_cold_ops"]) == pytest.approx(4 * switch / 3, abs=2)
    assert entries[h(999)]["src"] == "model"


def test_fit_rejected_on_scrambled_savings_ranks_by_measurement(tmp_path):
    a, b = model_arms(tmp_path, scramble=True)
    entries, order, calib, lines, _ = run(tmp_path, [a], [b], "--rho", "0.25")
    fit_line = next(line for line in lines if line.startswith("# Fit:"))
    assert "rejected" in fit_line, fit_line
    assert set(calib) == {"rho", "lambda"}
    assert all(e["src"] == "ab" for e in entries.values())
    scores = [float(entries[x]["score"]) for x in order]
    assert scores == sorted(scores, reverse=True)


def cumulative(path_key, increments):
    """Cumulative dumps from each dump's own blocks: [(tag, blocks, {hash: ns per opcode})], 4,000 parts of 1,500 opcodes each time."""
    dumps, acc = [], {}
    for tag, blocks, gains in increments:
        for code, ns in gains.items():
            p, o, n = acc.get(code, (0, 0, 0))
            acc[code] = (p + 4000, o + 4000 * 1500, n + int(4000 * 1500 * ns))
        contracts = [(code, 2000, p, 200, {(path_key, "M"): {10: (p, o, n)}}) for code, (p, o, n) in acc.items()]
        dumps.append(dump_lines(contracts, blocks, tag))
    return dumps


def test_training_range_and_halves(tmp_path):
    # Snapshots at 2,500 and 5,000 blocks and the shutdown dump at 10,000: contract 1 gains 5 ns per opcode throughout,
    # contract 2 gains 5 in the first 2,500 blocks and loses 1 after.
    a = write(tmp_path / "A.log", *cumulative("I", [(2500, 2500, {h(1): 10, h(2): 10}), (5000, 5000, {h(1): 10, h(2): 10}), (None, 10000, {h(1): 10, h(2): 10})]))
    b = write(tmp_path / "B.log", *cumulative("C", [(2500, 2500, {h(1): 5, h(2): 5}), (5000, 5000, {h(1): 5, h(2): 11}), (None, 10000, {h(1): 5, h(2): 11})]))
    entries, order, _, lines, _ = run(tmp_path, [a], [b], "--rho", "0.25", "--train", "1-5000", "--min-blocks", "1")
    source = next(line for line in lines if line.startswith("# Source:"))
    assert "N=5000" in source and "halves 1-2500,2501-5000" in source
    # Over blocks 1-5000 contract 2 nets a gain (5 then -1 ns per opcode), but not in both halves.
    assert h(1) in entries and h(2) not in entries
    assert float(entries[h(1)]["sM"]) == pytest.approx(5 * 8000 * 1500 / 5000, rel=1e-6)
    # The whole run: contract 2 over blocks 1-5000 and 5001-10000, positive in the first half only.
    entries, _, _, lines, _ = run(tmp_path, [a], [b], "--rho", "0.25", "--min-blocks", "1")
    assert "halves 1-5000,5001-end" in next(line for line in lines if line.startswith("# Source:"))
    assert h(2) not in entries and float(entries[h(1)]["sM"]) == pytest.approx(5 * 12000 * 1500 / 10000, rel=1e-6)
    # A range with no snapshot at its end is refused.
    with pytest.raises(SystemExit, match="no snapshot at 4000"):
        make_list.main(["--a", a, "--b", b, "--out", str(tmp_path / "x.txt"), "--train", "1-4000"])


def test_a_gain_in_one_half_only_is_left_out(tmp_path):
    """Repeats that agree leave sigma at the parts' own, which contract 2's net gain clears; its halves still disagree."""
    inc_a = [(2500, 2500, {h(1): 10, h(2): 10}), (None, 5000, {h(1): 10, h(2): 10})]
    inc_b = [(2500, 2500, {h(1): 5, h(2): 5}), (None, 5000, {h(1): 5, h(2): 11})]
    a = [write(tmp_path / f"A{i}.log", *cumulative("I", inc_a)) for i in (1, 2)]
    b = [write(tmp_path / f"B{i}.log", *cumulative("C", inc_b)) for i in (1, 2)]
    entries, _, _, lines, _ = run(tmp_path, a, b, "--rho", "0.25", "--min-blocks", "1")
    assert "halves 1-2500,2501-end" in next(line for line in lines if line.startswith("# Source:"))
    assert h(1) in entries and h(2) not in entries
    # Without the halves' rule it would be listed: its score clears z * sigma.
    arm_a, arm_b = make_list.Arm("A", a, 1, None), make_list.Arm("B", b, 1, None)
    measured = make_list.measure(arm_a, arm_b)
    assert make_list.jackknife(arm_a, arm_b, measured)
    m = measured[h(2)]
    assert m.score(0.25, 0) > 2 * m.sigma(0.25) > 0


def test_repeats_give_the_noise(tmp_path):
    """Two logs per arm: a contract whose repeats disagree has the jackknife's sigma, above the parts' own."""
    def one(name, ns_per_op):
        rows_key = "I" if name.startswith("A") else "C"
        rows = {(rows_key, "M"): {10: bucket(5000, 1500, ns_per_op)}}
        return write(tmp_path / f"{name}.log", dump_lines([(h(1), 2000, 5000, 900, rows)], 1000))

    a = [one("A1", 10), one("A2", 10)]
    b = [one("B1", 5), one("B2", 7)]
    entries, _, _, lines, _ = run(tmp_path, a, b, "--rho", "0.25")
    # Leave-one-out estimates over B, each over arm A's 15M opcodes in 2,000 blocks: B1 alone saves 5 ns per opcode, B2 3.
    s_b1, s_b2 = 5 * 7500, 3 * 7500
    jack = 0.5 * ((s_b2 - (s_b1 + s_b2) / 2) ** 2 + (s_b1 - (s_b1 + s_b2) / 2) ** 2)
    assert float(entries[h(1)]["sigma"]) == pytest.approx(math.sqrt(jack), rel=1e-3)
    assert "A=A1.log,A2.log" in next(line for line in lines if line.startswith("# Source:"))
