"""Builds IL-EVM's shipped list from two Stats runs over the same blocks: the calibration fit and the list tool.

Arm A ran with nothing compiled, arm B with the candidates compiled (Stats=true, PrecompileList=Candidates in B); each
logged the Stats dump at shutdown (IL-EVM stats lines) and, for a training range short of the whole run, cumulative
snapshots every few thousand blocks (IL-EVM stats@<blocks> lines, StatsSnapshotBlocks). Several logs per arm (ABBA) are
summed, and their spread gives the noise.

Per contract c, kind of thread k (M: block processing, O: every other thread) and part-length bucket b, with u the
nanoseconds per opcode, arm A's interpreted parts against arm B's parts on every path:
    sav = (u_A - u_B) * o_A / N_A, for buckets with 20 parts or more in both arms; S_k(c) = sum over b of sav.
A contract's score is S_M + rho * S_O - lambda * F, F the MiB of IL and tables its code compiles to (52.7 B per code
byte). The model fitted to the buckets, y = a[L,k] * m - g_k - b_k * Cold(d) * min(m, U) with y the saving per part of
m opcodes, gives the gate's thresholds and the live model's constants (the # calib line) when it predicts the contracts
it did not see; otherwise the priors ship, and the list is ranked by the measured savings alone.

usage:
  python -I tools/make_list.py --a A*.log --b B*.log [--train 1-5000|all] [--rho auto|0.25] [--dproc-ms X]
      [--max-bytes 4194304] [--max-entries 300] [--min-blocks 50] [--z 2] [--names names.csv] [--old Precompile.txt]
      --out Precompile.txt --report report.md
  python -I tools/make_list.py fit --a A*.log --b B*.log [--train 1-5000|all] [--rho ...] [--dproc-ms X]

--dproc-ms is the processing time per block arm B saved over arm A, in milliseconds (positive when B is faster), for
--rho auto; without it rho is 0.25. --max-bytes caps the list's code bytes, as the startup compile's time scales with them.
"""
import argparse
import glob
import math
import os
import re
import sys

KINDS = ("M", "O")
PATHS = ("I", "C", "H")
SHAPES = ("line", "mixed", "loop")
BUCKETS = 16
IL_BYTES_PER_CODE_BYTE = 52.7
MIB = float(1 << 20)
# A bucket counts toward a saving from this many parts in both arms, and toward the fit from the second.
MIN_PARTS_SAVING = 20
MIN_PARTS_FIT = 50
# The spread of one part's time per opcode about its bucket's mean, relative: the noise of a bucket with no repeats.
PART_CV = 0.5
D0_GRID = (8, 16, 32, 64, 128, 256)
U_GRID = (100, 300, 1000, 3000)
FOLDS = 5
HUBER_DELTA = 1.345
ACCEPT_R2 = 0.5
ACCEPT_TOTAL = 0.25
ACCEPT_SIGNS = 0.8
RHO_PRIOR = 0.25
# The laptop calibration (E3): a per opcode for line/mixed/loop code, g per visit, b per cold opcode, at the shared
# caches' level for block code; the plugin's IlModel.BlocksPrior.
PRIOR_A = {"line": 0.25, "mixed": 0.6, "loop": 1.1}
PRIOR_G = 25.0
PRIOR_B = 1.5
PRIOR_U = 300
PRIOR_D0 = 32
# XEN's code, which the report shows whatever its rank: the question the A/B answers for it is whether its cold cost wins.
XEN_PREFIX = "0xa17eb6bd"

HEADER = re.compile(r"IL-EVM stats(?:@(\d+))?: (\d+) contracts, (\d+) blocks, totals(.*)$")
LINE = re.compile(r"IL-EVM stats(?:@(\d+))? (\d+) (0x[0-9a-fA-F]{64}) size=(\d+) entries=(\d+) blocks=(\d+)(?: shipped=([01]))?(.*)$")
END = re.compile(r"IL-EVM stats(?:@(\d+))? end\s*$")
COUNTS = re.compile(r"^([ICH])\.([MO])=(\d+)/(\d+)/(\d+)$")
HIST = re.compile(r"^h([ICH])\.([MO])=(\S+)$")
VISITS = re.compile(r"^v([ICH])\.([MO])=(\S+)$")
BUCKET = re.compile(r"^(\d+):(\d+)/(\d+)/(\d+)(?:/\d+)?$")
TAG = re.compile(r"@block=(\d+)")
HASH = re.compile(r"^(?:0x)?([0-9a-fA-F]{64})$")


class Contract:
    """One code hash's counts in a dump: per path and kind its totals, and per bucket its parts, opcodes and nanoseconds."""

    __slots__ = ("hash", "size", "entries", "blocks", "shipped", "tot", "hist", "visits")

    def __init__(self, code_hash, size, entries=0, blocks=0, shipped=False):
        self.hash = code_hash
        self.size = size
        self.entries = entries
        self.blocks = blocks
        self.shipped = shipped
        self.tot = {}
        self.hist = {}
        # Stats v2's visit buckets, kept for the report; the savings use the part buckets.
        self.visits = {}

    def bucket(self, path, kind, b):
        h = self.hist.get((path, kind))
        return h[b] if h else (0, 0, 0)

    def parts(self, path, kind):
        return self.tot.get((path, kind), (0, 0, 0))[0]

    def compiled(self):
        """Whether any part of it ran compiled code, whole or handed back."""
        return any(self.parts(path, kind) > 0 for path in ("C", "H") for kind in KINDS)


class Dump:
    """One dump of a log: the shutdown one (tag None) or a snapshot after <tag> blocks."""

    def __init__(self, tag, blocks):
        self.tag = tag
        self.blocks = blocks
        self.totals = {}
        self.contracts = {}
        self.complete = False
        self.source = ""


def _counts_into(target, token):
    m = COUNTS.match(token)
    if not m:
        return False
    target[(m.group(1), m.group(2))] = (int(m.group(3)), int(m.group(4)), int(m.group(5)))
    return True


def _buckets(text):
    rows = [(0, 0, 0)] * BUCKETS
    for item in text.split(","):
        m = BUCKET.match(item)
        if m and int(m.group(1)) < BUCKETS:
            rows[int(m.group(1))] = (int(m.group(2)), int(m.group(3)), int(m.group(4)))
    return rows


def parse_log(path, lines=None):
    """The dumps of one log in its order: whatever precedes IL-EVM stats on a line (the logger's prefix) is skipped."""
    dumps = []
    current = None
    source = os.path.basename(path)
    if lines is None:
        with open(path, encoding="utf-8", errors="replace") as handle:
            lines = handle.readlines()
    for raw in lines:
        at = raw.find("IL-EVM stats")
        if at < 0:
            continue
        line = raw[at:].rstrip()
        m = HEADER.match(line)
        if m:
            tag = int(m.group(1)) if m.group(1) else None
            if tag is None and TAG.search(m.group(4)):
                tag = int(TAG.search(m.group(4)).group(1))
            current = Dump(tag, int(m.group(3)))
            current.source = source
            for token in m.group(4).split():
                _counts_into(current.totals, token)
            dumps.append(current)
            continue
        m = END.match(line)
        if m:
            if current is not None:
                current.complete = True
            current = None
            continue
        m = LINE.match(line)
        if not m or current is None:
            continue
        tag = int(m.group(1)) if m.group(1) else None
        if tag != current.tag and not (tag is None and TAG.search(m.group(8))):
            continue
        code_hash = m.group(3).lower()
        contract = Contract(code_hash, int(m.group(4)), int(m.group(5)), int(m.group(6)), m.group(7) == "1")
        for token in m.group(8).split():
            if _counts_into(contract.tot, token):
                continue
            h = HIST.match(token)
            if h:
                contract.hist[(h.group(1), h.group(2))] = _buckets(h.group(3))
                continue
            v = VISITS.match(token)
            if v:
                contract.visits[(v.group(1), v.group(2))] = _buckets(v.group(3))
        current.contracts[code_hash] = contract
    return dumps


def _combine(dumps, sign):
    """The sum of dumps (sign +1), or the first less the others (sign -1): counts add, sizes are the code's."""
    out = Dump(dumps[0].tag, 0)
    out.complete = all(d.complete for d in dumps)
    out.source = "+".join(d.source for d in dumps)
    for i, dump in enumerate(dumps):
        s = 1 if i == 0 else sign
        out.blocks += s * dump.blocks
        for key, value in dump.totals.items():
            old = out.totals.get(key, (0, 0, 0))
            out.totals[key] = tuple(o + s * v for o, v in zip(old, value))
        for code_hash, c in dump.contracts.items():
            mine = out.contracts.get(code_hash)
            if mine is None:
                mine = out.contracts[code_hash] = Contract(code_hash, c.size, shipped=c.shipped)
            mine.size = max(mine.size, c.size)
            mine.entries += s * c.entries
            mine.blocks += s * c.blocks
            mine.shipped = mine.shipped or c.shipped
            for key, value in c.tot.items():
                old = mine.tot.get(key, (0, 0, 0))
                mine.tot[key] = tuple(o + s * v for o, v in zip(old, value))
            for table, theirs in ((mine.hist, c.hist), (mine.visits, c.visits)):
                for key, rows in theirs.items():
                    old = table.get(key, [(0, 0, 0)] * BUCKETS)
                    table[key] = [tuple(o + s * v for o, v in zip(a, b)) for a, b in zip(old, rows)]
    if sign < 0:
        # A contract a snapshot left out (past its top) had its counts in the later dump only: nothing goes negative.
        for c in out.contracts.values():
            c.entries, c.blocks = max(0, c.entries), max(0, c.blocks)
            c.tot = {k: tuple(max(0, x) for x in v) for k, v in c.tot.items()}
            c.hist = {k: [tuple(max(0, x) for x in r) for r in rows] for k, rows in c.hist.items()}
    return out


def add(dumps):
    return _combine(dumps, +1)


def subtract(later, earlier):
    return _combine([later, earlier], -1)


def parse_range(text):
    """--train: 'all', or 'S-E' in blocks, from 1."""
    if text == "all":
        return 1, None
    m = re.match(r"^(\d+)-(\d+)$", text)
    if not m or int(m.group(1)) < 1 or int(m.group(2)) < int(m.group(1)):
        raise SystemExit(f"--train: expected all or S-E, got {text}")
    return int(m.group(1)), int(m.group(2))


def final_dump(dumps, path):
    finals = [d for d in dumps if d.tag is None]
    if not finals:
        raise SystemExit(f"{path}: no shutdown dump (IL-EVM stats: ...)")
    if len(finals) > 1:
        print(f"warning: {path}: {len(finals)} shutdown dumps, using the last", file=sys.stderr)
    if not finals[-1].complete:
        print(f"warning: {path}: the shutdown dump has no end line", file=sys.stderr)
    return finals[-1]


def snapshot(dumps, tag, path):
    for d in dumps:
        if d.tag == tag:
            return d
    raise SystemExit(f"{path}: no snapshot at {tag} blocks (IL-EVM stats@{tag}); set StatsSnapshotBlocks so one is taken there")


def window(dumps, start, end, path):
    """The counts of blocks start..end of one log: a dump, less the snapshot before start."""
    top = final_dump(dumps, path) if end is None else snapshot(dumps, end, path)
    if start <= 1:
        return top
    return subtract(top, snapshot(dumps, start - 1, path))


def middle_tag(logs, start, end):
    """A snapshot every log of both arms has, near the training range's middle: where its halves split; None for none."""
    ends = []
    for dumps, path in logs:
        ends.append(final_dump(dumps, path).blocks if end is None else end)
    stop = min(ends)
    first = start - 1
    common = None
    for dumps, _ in logs:
        tags = {d.tag for d in dumps if d.tag is not None and first < d.tag < stop}
        common = tags if common is None else common & tags
    if not common:
        return None
    middle = (first + stop) / 2
    tag = min(common, key=lambda t: (abs(t - middle), t))
    # Halves of a quarter or less are no halves.
    return tag if 0.25 <= (tag - first) / (stop - first) <= 0.75 else None


class Arm:
    """An arm's training counts: the logs summed, each log's own, and the two halves when snapshots split them."""

    def __init__(self, name, paths, start, end):
        self.name = name
        self.paths = paths
        self.logs = []
        for path in paths:
            self.logs.append((parse_log(path), path))
        self.start, self.end = start, end
        self.runs = [window(dumps, start, end, path) for dumps, path in self.logs]
        self.total = add(self.runs)
        self.halves = None

    def split(self, tag):
        first = [window(dumps, self.start, tag, path) for dumps, path in self.logs]
        second = []
        for dumps, path in self.logs:
            top = final_dump(dumps, path) if self.end is None else snapshot(dumps, self.end, path)
            second.append(subtract(top, snapshot(dumps, tag, path)))
        self.halves = (add(first), add(second))


def check_interpreted(arm):
    """Arm A must have run nothing compiled, in any of its dumps: its savings are measured against it."""
    for dumps, path in arm.logs:
        for dump in dumps:
            for c in dump.contracts.values():
                if c.compiled():
                    raise SystemExit(f"arm A ({path}) has compiled parts for {c.hash}: run it with nothing compiled")


def rows_of(ca, cb, kind):
    """Per bucket: arm A's interpreted parts, opcodes and ns, then arm B's on every path."""
    rows = []
    for b in range(BUCKETS):
        pa, oa, na = ca.bucket("I", kind, b) if ca else (0, 0, 0)
        pb = ob = nb = 0
        if cb:
            for path in PATHS:
                p, o, n = cb.bucket(path, kind, b)
                pb, ob, nb = pb + p, ob + o, nb + n
        rows.append((pa, oa, na, pb, ob, nb))
    return rows


def saving(rows, n_a):
    """S, its variance from the part counts, and each bucket's saving, per block of arm A."""
    s = var = 0.0
    per = [0.0] * BUCKETS
    for b, (pa, oa, na, pb, ob, nb) in enumerate(rows):
        if pa < MIN_PARTS_SAVING or pb < MIN_PARTS_SAVING or oa <= 0 or ob <= 0 or n_a <= 0:
            continue
        ua, ub = na / oa, nb / ob
        per[b] = (ua - ub) * oa / n_a
        s += per[b]
        var += PART_CV ** 2 * (ua * ua / pa + ub * ub / pb) * (oa / n_a) ** 2
    return s, var, per


def shape_of(c):
    """Line, mixed or loop by the opcodes a frame runs per byte of code, as the plugin's live model reads a visit."""
    ops = sum(c.tot.get(("I", k), (0, 0, 0))[1] for k in KINDS)
    frames = max(1, c.entries)
    passes = ops / frames / max(1, c.size)
    return "line" if passes < 0.1 else "loop" if passes >= 1 else "mixed"


def footprint_mib(size):
    return max(1, size) * IL_BYTES_PER_CODE_BYTE / MIB


class Measured:
    """A contract's measured savings, per kind, over the training blocks."""

    def __init__(self, code_hash, ca, cb, n_a, n_b):
        self.hash = code_hash
        ref = ca or cb
        self.size = ref.size
        self.entries = ca.entries if ca else 0
        self.blocks = ca.blocks if ca else cb.blocks
        self.candidate = cb is not None and cb.compiled()
        self.shape = shape_of(ca) if ca else "mixed"
        self.rows = {k: rows_of(ca, cb, k) for k in KINDS}
        self.s, self.var, self.per = {}, {}, {}
        for k in KINDS:
            self.s[k], self.var[k], self.per[k] = saving(self.rows[k], n_a)
        self.parts_a = {k: (ca.tot.get(("I", k), (0, 0, 0))[0] if ca else 0) for k in KINDS}
        self.ops_a = {k: (ca.tot.get(("I", k), (0, 0, 0))[1] if ca else 0) for k in KINDS}
        self.n_a = n_a
        self.halves = None
        self.jack = None

    def score(self, rho, lam):
        return self.s["M"] + rho * self.s["O"] - lam * footprint_mib(self.size)

    def sigma(self, rho):
        formula = math.sqrt(self.var["M"] + rho * rho * self.var["O"])
        if self.jack is None:
            return formula
        # One or a few repeats spread less than the parts' noise by luck as often as not: the formula is the floor.
        return max(formula, math.sqrt(self.jack[0] + rho * rho * self.jack[1]))


def measure(arm_a, arm_b, a_dump=None, b_dump=None):
    """Every contract either arm's training dump has, measured."""
    a_dump = a_dump or arm_a.total
    b_dump = b_dump or arm_b.total
    out = {}
    for code_hash in set(a_dump.contracts) | set(b_dump.contracts):
        ca, cb = a_dump.contracts.get(code_hash), b_dump.contracts.get(code_hash)
        out[code_hash] = Measured(code_hash, ca, cb, a_dump.blocks, b_dump.blocks)
    return out


def jackknife(arm_a, arm_b, measured):
    """With two logs or more in each arm, each contract's leave-one-log-out variance, per kind, both arms' added."""
    if len(arm_a.runs) < 2 or len(arm_b.runs) < 2:
        return False
    sums = {h: {k: [] for k in KINDS} for h in measured}
    for arm, other, is_a in ((arm_a, arm_b, True), (arm_b, arm_a, False)):
        n = len(arm.runs)
        estimates = {h: {k: [] for k in KINDS} for h in measured}
        for i in range(n):
            rest = add([r for j, r in enumerate(arm.runs) if j != i])
            a_dump, b_dump = (rest, other.total) if is_a else (other.total, rest)
            for h in measured:
                ca, cb = a_dump.contracts.get(h), b_dump.contracts.get(h)
                for k in KINDS:
                    estimates[h][k].append(saving(rows_of(ca, cb, k), a_dump.blocks)[0])
        for h in measured:
            for k in KINDS:
                xs = estimates[h][k]
                mean = sum(xs) / n
                sums[h][k].append((n - 1) / n * sum((x - mean) ** 2 for x in xs))
    for h, m in measured.items():
        m.jack = (sum(sums[h]["M"]), sum(sums[h]["O"]))
    return True


def halves(arm_a, arm_b, measured, tag):
    """Each contract's savings over either half of the training blocks; from two halves alone, their jackknife too."""
    arm_a.split(tag)
    arm_b.split(tag)
    first = measure(arm_a, arm_b, arm_a.halves[0], arm_b.halves[0])
    second = measure(arm_a, arm_b, arm_a.halves[1], arm_b.halves[1])
    for h, m in measured.items():
        one, two = first.get(h), second.get(h)
        m.halves = (one, two)
        if m.jack is None:
            sm = [x.s["M"] if x else 0.0 for x in (one, two)]
            so = [x.s["O"] if x else 0.0 for x in (one, two)]
            m.jack = (((sm[0] - sm[1]) / 2) ** 2, ((so[0] - so[1]) / 2) ** 2)


# The fit: y = a[L,k] * m - g_k - b_k * Cold(d) * min(m, U), linear in its ten constants at each (d0, U).
COLUMNS = [f"a.{shape}.{kind}" for kind in KINDS for shape in SHAPES] + [f"g.{k}" for k in KINDS] + [f"b.{k}" for k in KINDS]


def prior_vector():
    return [PRIOR_A[shape] for kind in KINDS for shape in SHAPES] + [PRIOR_G, PRIOR_G, PRIOR_B, PRIOR_B]


def features(kind, shape, m, cold, u):
    x = [0.0] * len(COLUMNS)
    x[COLUMNS.index(f"a.{shape}.{kind}")] = m
    x[COLUMNS.index(f"g.{kind}")] = -1.0
    x[COLUMNS.index(f"b.{kind}")] = -cold * min(m, u)
    return x


def solve(matrix, vector):
    """Gaussian elimination with partial pivoting."""
    n = len(vector)
    a = [row[:] + [vector[i]] for i, row in enumerate(matrix)]
    for col in range(n):
        pivot = max(range(col, n), key=lambda r: abs(a[r][col]))
        if abs(a[pivot][col]) < 1e-300:
            raise ArithmeticError("singular")
        a[col], a[pivot] = a[pivot], a[col]
        for r in range(col + 1, n):
            f = a[r][col] / a[col][col]
            if f:
                for c in range(col, n + 1):
                    a[r][c] -= f * a[col][c]
    x = [0.0] * n
    for r in range(n - 1, -1, -1):
        x[r] = (a[r][n] - sum(a[r][c] * x[c] for c in range(r + 1, n))) / a[r][r]
    return x


def huber_fit(points):
    """Weighted Huber regression by reweighting: the points' noise weights them, residuals past 1.345 sigma count less.

    A constant no point informs, as a shape no contract has, stays at its prior, which a faint ridge pulls it to."""
    prior = prior_vector()
    n = len(COLUMNS)
    beta = prior[:]
    robust = [1.0] * len(points)
    for _ in range(100):
        xtx = [[0.0] * n for _ in range(n)]
        xty = [0.0] * n
        for p, h in zip(points, robust):
            w = h / (p["sigma"] ** 2)
            x = p["x"]
            for i in range(n):
                if x[i] == 0.0:
                    continue
                xty[i] += w * x[i] * p["y"]
                for j in range(n):
                    if x[j] != 0.0:
                        xtx[i][j] += w * x[i] * x[j]
        for i in range(n):
            ridge = xtx[i][i] * 1e-9 + 1e-12
            xtx[i][i] += ridge
            xty[i] += ridge * prior[i]
        new = solve(xtx, xty)
        change = max(abs(a - b) / max(1e-9, abs(b)) for a, b in zip(new, beta))
        beta = new
        robust = []
        for p in points:
            z = abs(p["y"] - dot(p["x"], beta)) / p["sigma"]
            robust.append(1.0 if z <= HUBER_DELTA else HUBER_DELTA / z)
        if change < 1e-9:
            break
    return beta


def dot(x, beta):
    return sum(a * b for a, b in zip(x, beta))


def fold_of(code_hash):
    return int(code_hash[2:10], 16) % FOLDS


def reuse_colds(dump, measured, d0):
    """Cold(d) per contract and kind: d the other parts of the kind between two of the contract's, from arm A's totals."""
    colds = {}
    for k in KINDS:
        total = dump.totals.get(("I", k), (0, 0, 0))[0]
        if total <= 0:
            total = sum(m.parts_a[k] for m in measured.values())
        for h, m in measured.items():
            parts = m.parts_a[k]
            d = total / parts if parts > 0 else float("inf")
            colds[(h, k)] = 1.0 if math.isinf(d) else d / (d + d0)
    return colds


def bucket_points(m, colds, u, min_parts, need_b=True):
    """A contract's buckets as points of the fit: the saving per part y over m opcodes per part, and its noise."""
    out = []
    for k in KINDS:
        for b, (pa, oa, na, pb, ob, nb) in enumerate(m.rows[k]):
            if pa < min_parts or oa <= 0 or (need_b and (pb < min_parts or ob <= 0)):
                continue
            ua = na / oa
            mean = oa / pa
            point = {"hash": m.hash, "kind": k, "bucket": b, "m": mean, "weight": pa / m.n_a,
                     "x": features(k, m.shape, mean, colds[(m.hash, k)], u)}
            if need_b:
                ub = nb / ob
                point["y"] = (ua - ub) * mean
                point["sigma"] = max(1e-6, PART_CV * mean * math.sqrt(ua * ua / pa + ub * ub / pb))
            out.append(point)
    return out


def predicted_saving(m, colds, u, beta):
    """The model's S per kind for a contract, over the buckets a saving counts."""
    s = {k: 0.0 for k in KINDS}
    per = {k: [0.0] * BUCKETS for k in KINDS}
    need_b = m.candidate
    for p in bucket_points(m, colds, u, MIN_PARTS_SAVING, need_b=need_b):
        value = dot(p["x"], beta) * p["weight"]
        s[p["kind"]] += value
        per[p["kind"]][p["bucket"]] = value
    return s, per


class Fit:
    def __init__(self):
        self.accepted = False
        self.reason = "no fit"
        self.beta = prior_vector()
        self.d0, self.u = PRIOR_D0, PRIOR_U
        self.r2 = self.total_ratio = self.signs = float("nan")
        self.tau = 0.0
        self.points = self.contracts = 0
        self.grid = []

    def value(self, name):
        return self.beta[COLUMNS.index(name)]


def fit(arm_a, measured):
    """Fits the model at each grid point, 5-fold cross-validated by contract, and keeps the best when it passes."""
    result = Fit()
    candidates = {h: m for h, m in measured.items() if m.candidate}
    for d0 in D0_GRID:
        colds = reuse_colds(arm_a.total, measured, d0)
        for u in U_GRID:
            points = [p for m in candidates.values() for p in bucket_points(m, colds, u, MIN_PARTS_FIT)]
            if len({p["hash"] for p in points}) < FOLDS or len(points) < 2 * len(COLUMNS):
                result.reason = f"too little data: {len(points)} points over {len({p['hash'] for p in points})} contracts"
                return result
            cv = {}
            try:
                for f in range(FOLDS):
                    beta = huber_fit([p for p in points if fold_of(p["hash"]) != f])
                    for h, m in candidates.items():
                        if fold_of(h) == f:
                            s, _ = predicted_saving(m, colds, u, beta)
                            cv[h] = s["M"] + s["O"]
            except ArithmeticError:
                continue
            stats = acceptance(candidates, cv)
            result.grid.append((d0, u) + stats)
    if not result.grid:
        result.reason = "no grid point could be fitted"
        return result
    best = max(result.grid, key=lambda row: (row[2], -abs(row[3] - 1)))
    result.d0, result.u, result.r2, result.total_ratio, result.signs, result.tau = best[0], best[1], best[2], best[3], best[4], best[5]
    colds = reuse_colds(arm_a.total, measured, result.d0)
    points = [p for m in candidates.values() for p in bucket_points(m, colds, result.u, MIN_PARTS_FIT)]
    result.points, result.contracts = len(points), len({p["hash"] for p in points})
    result.beta = huber_fit(points)
    failed = []
    if not result.r2 >= ACCEPT_R2:
        failed.append(f"cross-validated R2 {result.r2:.2f} < {ACCEPT_R2}")
    if not abs(result.total_ratio - 1) <= ACCEPT_TOTAL:
        failed.append(f"predicted total {result.total_ratio:.2f}x the measured")
    if not result.signs >= ACCEPT_SIGNS:
        failed.append(f"signs agree on {result.signs:.0%} of the significant contracts")
    result.accepted = not failed
    result.reason = "accepted" if result.accepted else "rejected: " + "; ".join(failed)
    return result


def acceptance(candidates, cv):
    """Contract-level R2 of the cross-validated predictions, their total over the measured one, the signs' agreement, and the residual's spread."""
    hashes = [h for h in cv]
    meas = [candidates[h].s["M"] + candidates[h].s["O"] for h in hashes]
    pred = [cv[h] for h in hashes]
    mean = sum(meas) / len(meas) if meas else 0.0
    ss_tot = sum((y - mean) ** 2 for y in meas)
    ss_res = sum((y - p) ** 2 for y, p in zip(meas, pred))
    r2 = 1 - ss_res / ss_tot if ss_tot > 0 else float("-inf")
    total = sum(meas)
    ratio = sum(pred) / total if total else float("inf")
    significant = [(y, p) for h, y, p in zip(hashes, meas, pred)
                   if abs(y) > 2 * math.sqrt(candidates[h].var["M"] + candidates[h].var["O"])]
    signs = sum(1 for y, p in significant if (y > 0) == (p > 0)) / len(significant) if significant else 0.0
    tau = math.sqrt(ss_res / len(meas)) if meas else 0.0
    return r2, ratio, signs, tau


def derive_lambda(measured):
    """The footprint's charge: the uncompiled code's slowdown in arm B per MiB of what B compiled; 0 within 2 sigma."""
    delta = var = mib = 0.0
    for m in measured.values():
        if m.candidate:
            mib += footprint_mib(m.size)
        elif m.parts_a["M"] > 0:
            delta -= m.s["M"]
            var += m.var["M"]
    sigma = math.sqrt(var)
    lam = delta / mib if mib > 0 and delta > 2 * sigma else 0.0
    return lam, delta, sigma, mib


def derive_rho(text, dproc_ms, measured, lam):
    """--rho as given, or auto: what the other threads' savings add to processing, from the processing difference."""
    if text != "auto":
        return min(1.0, max(0.0, float(text))), "given"
    if dproc_ms is None:
        return RHO_PRIOR, "prior"
    sm = sum(m.s["M"] for m in measured.values() if m.candidate)
    so = sum(m.s["O"] for m in measured.values() if m.candidate)
    footprint = lam * sum(footprint_mib(m.size) for m in measured.values() if m.candidate)
    if so <= 0:
        return RHO_PRIOR, "prior (no O saving)"
    # Processing saved = S_M + rho * S_O - lambda * F, the footprint's cost being the other code's slowdown.
    rho = (dproc_ms * 1e6 - sm + footprint) / so
    return min(1.0, max(0.0, rho)), f"from --dproc-ms {dproc_ms}"


def gate_tokens(result):
    """The block runner's thresholds from the fit: the switch point where a cold visit breaks even, and 4/3 of it to admit cold."""
    tokens = {}
    a = result.value("a.mixed.M")
    if a > 0:
        switch = (result.value("g.M") + result.value("b.M") * result.u) / a
        switch = int(round(min(1 << 20, max(64, switch))))
        tokens["gate_block_switch_ops"] = switch
        tokens["gate_block_visit_cold_ops"] = int(round(4 * switch / 3))
    a = result.value("a.mixed.O")
    if a > 0:
        # The other threads' own break-even, for reference: the block runner has one set, and use= keeps a contract off
        # the workers where they do not gain.
        tokens["gate_other_switch_ops"] = int(round(min(1 << 20, max(64, (result.value("g.O") + result.value("b.O") * result.u) / a))))
    return tokens


def calibration(result, rho, lam):
    tokens = {}
    if result.accepted:
        for name in COLUMNS:
            tokens[name] = round(result.value(name), 4)
        tokens["U"] = result.u
        tokens["d0"] = result.d0
        tokens["tau"] = round(result.tau, 1)
        tokens.update(gate_tokens(result))
    tokens["rho"] = round(rho, 4)
    tokens["lambda"] = round(lam, 2)
    return tokens


def best_split(per_m, per_o, rho):
    """The bucket from which on the savings sum highest, when that sum is positive: the contract's minv."""
    best, at = 0.0, None
    for b in range(BUCKETS):
        total = sum(per_m[x] + rho * per_o[x] for x in range(b, BUCKETS))
        if total > best:
            best, at = total, b
    return at


class Entry:
    def __init__(self, m, score, sigma, s_m, s_o, src, use, minv):
        self.m, self.score, self.sigma, self.s_m, self.s_o = m, score, sigma, s_m, s_o
        self.src, self.use, self.minv = src, use, minv


def choose(measured, result, arm_a, args, rho, lam):
    """The eligible contracts, measured and modelled, then the greedy fill by score per byte under the caps."""
    eligible = []
    rejected = []
    for m in measured.values():
        if not m.candidate:
            continue
        score, sigma = m.score(rho, lam), m.sigma(rho)
        why = None
        if not score > args.z * sigma:
            why = "below z*sigma"
        elif m.blocks < args.min_blocks:
            why = f"in {m.blocks} blocks"
        elif m.halves is not None and not all(h is not None and h.score(rho, lam) > 0 for h in m.halves):
            why = "not positive in both halves"
        sigma_m, sigma_o = math.sqrt(max(m.var["M"], m.jack[0] if m.jack else 0)), math.sqrt(max(m.var["O"], m.jack[1] if m.jack else 0))
        use = ("m" if m.s["M"] > args.z * sigma_m or (m.s["M"] > 0 and score > args.z * sigma) else "") + \
              ("w" if m.s["O"] > args.z * sigma_o else "")
        entry = Entry(m, score, sigma, m.s["M"], m.s["O"], "ab", use or "-", best_split(m.per["M"], m.per["O"], rho))
        (rejected if why else eligible).append((entry, why))
    if result.accepted:
        colds = reuse_colds(arm_a.total, measured, result.d0)
        for m in measured.values():
            if m.candidate or m.blocks < args.min_blocks:
                continue
            s, per = predicted_saving(m, colds, result.u, result.beta)
            score = s["M"] + rho * s["O"] - lam * footprint_mib(m.size)
            if score > 4 * result.tau:
                use = ("m" if s["M"] > 0 else "") + ("w" if s["O"] > 0 else "")
                eligible.append((Entry(m, score, result.tau, s["M"], s["O"], "model", use or "-", best_split(per["M"], per["O"], rho)), None))
    pool = sorted((e for e, _ in eligible), key=lambda e: (-e.score / footprint_mib(e.m.size), e.m.hash))
    chosen, used = [], 0
    for e in pool:
        if len(chosen) >= args.max_entries:
            break
        if used + e.m.size > args.max_bytes:
            continue
        chosen.append(e)
        used += e.m.size
    chosen.sort(key=lambda e: (-e.score, e.m.hash))
    return chosen, [e for e, _ in eligible], rejected


def entry_line(e):
    m = e.m
    parts = m.parts_a["M"] + m.parts_a["O"]
    ops = m.ops_a["M"] + m.ops_a["O"]
    n_a = max(1, m.n_a)
    fields = [m.hash, f"score={e.score:.0f}", f"sM={e.s_m:.0f}", f"sO={e.s_o:.0f}", f"sigma={e.sigma:.0f}", f"size={m.size}",
              f"n={parts / n_a:.3f}", f"w={ops / parts if parts else 0:.1f}", f"f={min(1.0, m.blocks / n_a):.3f}", f"L={m.shape}",
              f"use={e.use}"]
    # minv names the log2 visit length the block runner admits from; 1 is the gate's floor.
    if e.minv is not None:
        fields.append(f"minv={max(1, e.minv)}")
    fields += [f"src={e.src}", f"ops={ops}", f"frames={m.entries}"]
    return " ".join(fields)


def run_ids(paths):
    ids = []
    for p in paths:
        found = re.findall(r"\d{8,}", os.path.basename(p))
        ids.append(found[-1] if found else os.path.basename(p))
    return ",".join(ids)


def header_lines(args, arm_a, arm_b, result, rho, rho_source, lam_info, tokens, chosen, segments):
    lam, delta, sigma, mib = lam_info
    train = args.train
    lines = [
        "# Contracts IL-EVM compiles at startup, before blocks or calls run: one keccak code hash per line, by measured saving per block.",
        "# After the hash: score= sM= sO= sigma= in ns per block, size= in bytes, n= parts per block, w= opcodes per part,"
        " f= the share of blocks, L= the shape, use= the block threads, minv= the log2 visit length the block runner admits"
        " from, src= measured (ab) or modelled; ops= and frames= the interpreted opcodes and frames of arm A, for PrecompileMinOpsPerFrame.",
        f"# Source: tools/make_list.py, Stats A/B runs A={run_ids(arm_a.paths)} B={run_ids(arm_b.paths)}, N={arm_a.total.blocks}"
        f" blocks (B {arm_b.total.blocks}), training {train}, halves {segments or 'none'}.",
        f"# Rule: score = sM + rho*sO - lambda*F (F = size*{IL_BYTES_PER_CODE_BYTE} B of IL and tables); eligible past"
        f" {args.z}*sigma, in {args.min_blocks} blocks or more{', positive in both halves' if segments else ''}; modelled"
        f" ones past 4*tau; greedy by score per byte under {args.max_bytes} code bytes and {args.max_entries} entries.",
        f"# rho={rho:.4f} ({rho_source}); lambda={lam:.2f} ns per block per MiB (uncompiled code {delta:+.0f} ns per block,"
        f" sigma {sigma:.0f}, over {mib:.1f} MiB).",
        f"# Fit: {result.reason}; R2cv={result.r2:.3f}, predicted/measured={result.total_ratio:.3f}, signs={result.signs:.0%},"
        f" d0={result.d0}, U={result.u}, tau={result.tau:.0f}, {result.points} points over {result.contracts} contracts.",
        f"# Kept {len(chosen)} contracts, {sum(e.m.size for e in chosen)} bytes of code, {sum(e.score for e in chosen) / 1000:.1f} us per block.",
        "# calib " + " ".join(f"{k}={v}" for k, v in tokens.items()),
    ]
    return lines


def load_names(path):
    names = {}
    if not path:
        return names
    with open(path, encoding="utf-8") as handle:
        for line in handle:
            parts = [p.strip() for p in re.split(r"[,\t]", line.strip(), maxsplit=1)]
            m = HASH.match(parts[0]) if parts and parts[0] else None
            if m and len(parts) > 1:
                names["0x" + m.group(1).lower()] = parts[1]
    return names


def parse_list(path):
    """The hashes of a list, the plugin's first hex token of 64 digits per line; comments and stats lines skipped."""
    hashes = []
    seen = set()
    with open(path, encoding="utf-8") as handle:
        for raw in handle:
            line = raw.strip()
            if not line or line.startswith("#") or "IL-EVM stats" in line:
                continue
            for token in line.split():
                m = HASH.match(token)
                if m:
                    h = "0x" + m.group(1).lower()
                    if h not in seen:
                        seen.add(h)
                        hashes.append(h)
                    break
    return hashes


def report(path, args, measured, chosen, eligible, rejected, result, rho, lam_info, names, old):
    def name(h):
        return names.get(h, h[:18] + "...")

    n_a = max(1, next(iter(measured.values())).n_a) if measured else 1
    cands = [m for m in measured.values() if m.candidate]
    out = [f"# IL-EVM list report", "", f"- Fit: {result.reason}", f"- rho {rho:.3f}, lambda {lam_info[0]:.2f} ns per block per MiB",
           f"- {len(cands)} candidates measured, {len(eligible)} eligible, {len(chosen)} kept", ""]
    out += ["## Fit grid", "", "| d0 | U | R2cv | pred/meas | signs | tau |", "|---|---|---|---|---|---|"]
    for row in result.grid:
        out.append(f"| {row[0]} | {row[1]} | {row[2]:.3f} | {row[3]:.3f} | {row[4]:.0%} | {row[5]:.0f} |")
    if result.accepted:
        out += ["", "Constants: " + ", ".join(f"{c}={result.value(c):.3f}" for c in COLUMNS)]
    out += ["", "## The list by score", "", "| rank | contract | score ns/blk | sM | sO | sigma | size | use | minv | src |", "|---|---|---|---|---|---|---|---|---|---|"]
    for i, e in enumerate(chosen, 1):
        out.append(f"| {i} | {name(e.m.hash)} | {e.score:.0f} | {e.s_m:.0f} | {e.s_o:.0f} | {e.sigma:.0f} | {e.m.size} | {e.use} | {max(1, e.minv) if e.minv is not None else ''} | {e.src} |")
    by_ops = sorted(measured.values(), key=lambda m: -(m.ops_a["M"] + m.ops_a["O"]))[:50]
    by_parts = sorted(measured.values(), key=lambda m: -(m.parts_a["M"] + m.parts_a["O"]))[:50]
    kept = {e.m.hash: i for i, e in enumerate(chosen, 1)}
    for title, rows in (("By interpreted opcodes per block", by_ops), ("By parts per block", by_parts)):
        out += ["", f"## {title} (top 50)", "", "| rank | contract | ops/blk | parts/blk | score | list rank |", "|---|---|---|---|---|---|"]
        for i, m in enumerate(rows, 1):
            score = f"{m.score(rho, lam_info[0]):.0f}" if m.candidate else "-"
            out.append(f"| {i} | {name(m.hash)} | {(m.ops_a['M'] + m.ops_a['O']) / n_a:.0f} | {(m.parts_a['M'] + m.parts_a['O']) / n_a:.1f} | {score} | {kept.get(m.hash, '')} |")
    ranked = sorted(cands, key=lambda m: -m.score(rho, lam_info[0]))
    for title, rows in (("Top gainers", ranked[:25]), ("Top losers", list(reversed(ranked[-25:])))):
        out += ["", f"## {title}", "", "| contract | score | sigma | sM | sO | blocks |", "|---|---|---|---|---|---|"]
        for m in rows:
            out.append(f"| {name(m.hash)} | {m.score(rho, lam_info[0]):.0f} | {m.sigma(rho):.0f} | {m.s['M']:.0f} | {m.s['O']:.0f} | {m.blocks} |")
    if old is not None:
        shared = [h for h in old if h in kept]
        out += ["", "## Against the old list", "", f"- {len(shared)} of the old list's {len(old)} are kept; {len(chosen) - len(shared)} are new.",
                "- Old entries left out, by the old order: " + ", ".join(name(h) for h in old if h not in kept)[:4000]]
    out += ["", "## XEN", ""]
    xen = [m for h, m in measured.items() if h.startswith(XEN_PREFIX)]
    if not xen:
        out.append("- Not in either arm's dump.")
    for m in xen:
        why = next((w for e, w in rejected if e.m.hash == m.hash), None)
        state = f"kept at rank {kept[m.hash]}" if m.hash in kept else f"left out ({why})" if why else "eligible, not kept" if m.candidate else "not a candidate"
        out.append(f"- {m.hash}: score {m.score(rho, lam_info[0]):.0f} ns per block (sM {m.s['M']:.0f}, sO {m.s['O']:.0f}, sigma {m.sigma(rho):.0f}), {state}.")
    with open(path, "w", encoding="utf-8", newline="\n") as handle:
        handle.write("\n".join(out) + "\n")


def expand(patterns):
    paths = []
    for pattern in patterns:
        found = sorted(glob.glob(pattern))
        paths += found if found else [pattern]
    for p in paths:
        if not os.path.isfile(p):
            raise SystemExit(f"no such log: {p}")
    return paths


def main(argv=None):
    argv = list(sys.argv[1:] if argv is None else argv)
    fit_only = bool(argv) and argv[0] == "fit"
    if fit_only:
        argv = argv[1:]
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--a", nargs="+", required=True)
    parser.add_argument("--b", nargs="+", required=True)
    parser.add_argument("--train", default="all")
    parser.add_argument("--rho", default="auto")
    parser.add_argument("--dproc-ms", type=float, default=None)
    parser.add_argument("--max-bytes", type=int, default=4194304)
    parser.add_argument("--max-entries", type=int, default=300)
    parser.add_argument("--min-blocks", type=int, default=50)
    parser.add_argument("--z", type=float, default=2.0)
    parser.add_argument("--names", default=None)
    parser.add_argument("--old", default=os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "src", "Nethermind.IlEvm", "Precompile.txt"))
    parser.add_argument("--out", default=None)
    parser.add_argument("--report", default=None)
    args = parser.parse_args(argv)
    if not fit_only and not args.out:
        parser.error("--out is required")

    start, end = parse_range(args.train)
    arm_a = Arm("A", expand(args.a), start, end)
    arm_b = Arm("B", expand(args.b), start, end)
    check_interpreted(arm_a)
    measured = measure(arm_a, arm_b)
    if not any(m.candidate for m in measured.values()):
        raise SystemExit("arm B ran nothing compiled: no candidates to measure")
    jackknife(arm_a, arm_b, measured)
    tag = middle_tag(arm_a.logs + arm_b.logs, start, end)
    segments = None
    if tag is not None:
        halves(arm_a, arm_b, measured, tag)
        segments = f"{start}-{tag},{tag + 1}-{end if end is not None else 'end'}"
    result = fit(arm_a, measured)
    lam_info = derive_lambda(measured)
    rho, rho_source = derive_rho(args.rho, args.dproc_ms, measured, lam_info[0])
    tokens = calibration(result, rho, lam_info[0])
    if fit_only:
        print(f"fit: {result.reason}")
        for row in result.grid:
            print(f"  d0={row[0]:>3} U={row[1]:>4} R2cv={row[2]:.3f} pred/meas={row[3]:.3f} signs={row[4]:.0%} tau={row[5]:.0f}")
        print("# calib " + " ".join(f"{k}={v}" for k, v in tokens.items()))
        return 0

    chosen, eligible, rejected = choose(measured, result, arm_a, args, rho, lam_info[0])
    lines = header_lines(args, arm_a, arm_b, result, rho, rho_source, lam_info, tokens, chosen, segments)
    lines += [entry_line(e) for e in chosen]
    with open(args.out, "w", encoding="utf-8", newline="\n") as handle:
        handle.write("\n".join(lines) + "\n")
    if args.report:
        old = parse_list(args.old) if args.old and os.path.isfile(args.old) else None
        report(args.report, args, measured, chosen, eligible, rejected, result, rho, lam_info, load_names(args.names), old)
    print(f"{len(chosen)} contracts, {sum(e.m.size for e in chosen)} bytes; fit {result.reason}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
