#!/usr/bin/env python3
"""frame-tx-bench: a command center for the EIP-8141 MAX_VERIFY_GAS measurement campaign.

Built against the benchmarking plan (https://hackmd.io/@deanstef/BJu3k-Mvfl) and its local mirror,
"Benchmarking VERIFY frame gas ceiling.md". It does not reimplement the harnesses -- it catalogs the
NUnit [Explicit] fixtures already in the tree, drives `dotnet test` the same way
`.github/workflows/run-frame-tx-measurements.yml` does, and turns the `RESULT key=value` rows they
write into the tables and charts the plan's "Metrics" and "Decision rule" sections ask for.

Subcommands:
    list      Show the campaign's fixtures, or one fixture's exact (shape, ceiling, K_retry, rate)
              sweep -- the input combinations the plan calls for.
    run       Build and run one or more fixtures with `dotnet test`, mirroring the CI workflow's
              env vars, --output Detailed, single-core pinning, and (optional) MaxVerifyGas patch.
    report    Parse RESULT rows from one or more runs into per-case tables, a plan-shaped summary
              (t_reject, W_0, Delta, R_max, C*), and charts (PNG via matplotlib if present, else an
              ASCII fallback -- no required third-party dependency).

Examples:
    ./frame_tx_bench.py list
    ./frame_tx_bench.py list --fixture FrameTxFloodMeasurement
    ./frame_tx_bench.py run --fixture FrameTxMempoolDosMeasurement --groth16-artifacts /path/to/artifacts
    ./frame_tx_bench.py run --harness flood --ceiling 500000 --raise-verify-gas 510000
    ./frame_tx_bench.py report --in out/2026-09-02T1200/*.txt --summary --budget-us 2000 --attack-rate 100
"""
from __future__ import annotations

import argparse
import glob
import os
import re
import shutil
import statistics
import subprocess
import sys
import time
from collections import defaultdict
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
SRC_DIR = REPO_ROOT / "src" / "Nethermind"
CONST_FILE = "Nethermind.Core/Eip8141Constants.cs"
STOCK_LITERAL = "MaxVerifyGas = 300_000"

CEILINGS = [100_000, 236_285, 300_000, 500_000]
K_RETRIES = [1, 2, 4, 8]
GROTH16_SHAPES = ["groth16-236k", "groth16-300k", "groth16-500k", "groth16-soispoke"]


# --------------------------------------------------------------------------------------
# Catalog: one entry per NUnit [Explicit] fixture. `dotnet test` is driven per fixture --
# NUnit/VSTest's TestCaseFilter mini-language treats bare parentheses as grouping operators,
# so filtering to one exact [TestCase] is not reliably scriptable; running the whole fixture
# and slicing the resulting RESULT rows in `report` is what the CI workflow itself does too.
# --------------------------------------------------------------------------------------

@dataclass
class Fixture:
    name: str
    harness: str  # verify | mempool | flood | producer-retry | prefix-retry
    project: str
    methods: list[str]
    combos: list[tuple]  # (method, *params) -- the plan's actual sweep points, for `list`
    single_core: bool = False
    needs_groth16: bool = False
    max_ceiling: int = 300_000  # highest declared ceiling this fixture can exercise
    result_env: str = "FRAME_RETRY_OUT"


FIXTURES: list[Fixture] = [
    Fixture(
        name="FrameTxVerifyDosMeasurement",
        harness="verify",
        project="Nethermind.Evm.Test",
        methods=["UnpaidVerificationCost", "UnpaidBurnPerAttempt"],
        combos=[
            ("UnpaidVerificationCost", g, shape)
            for g in (300_000, 500_000, 1_048_576)
            for shape in ("jump", "keccak", "keccak-wide")
        ] + [("UnpaidBurnPerAttempt", g) for g in (100_000, 236_285)],
        max_ceiling=1_048_576,
        result_env="FRAME_RETRY_OUT",
    ),
    Fixture(
        name="FrameTxMempoolDosMeasurement",
        harness="mempool",
        project="Nethermind.TxPool.Test",
        methods=[
            "Reject_cost_of_a_budget_burning_prefix", "Reject_cost_of_a_banned_opcode_prefix",
            "Reject_cost_of_a_groth16_verifier_prefix", "Reject_cost_of_an_ordinary_transaction",
            "Reject_cost_of_a_signature_stuffed_prefix",
        ],
        combos=(
            [("Reject_cost_of_a_budget_burning_prefix", shape, c)
             for shape in ("jump", "keccak", "keccak-wide") for c in CEILINGS]
            + [("Reject_cost_of_a_banned_opcode_prefix", c) for c in CEILINGS]
            + [("Reject_cost_of_a_groth16_verifier_prefix", shape) for shape in GROTH16_SHAPES]
            + [("Reject_cost_of_an_ordinary_transaction",)]
            + [("Reject_cost_of_a_signature_stuffed_prefix", c) for c in CEILINGS]
        ),
        needs_groth16=True,
        max_ceiling=510_000,
        result_env="FRAME_MEMPOOL_DOS_OUT",
    ),
    Fixture(
        name="FrameTxFloodMeasurement",
        harness="flood",
        project="Nethermind.Blockchain.Test",
        methods=[
            "Admission_flood_actually_reaches_the_simulator",
            "Block_production_delay_under_flood_and_retries",
            "Block_processing_delay_under_admission_flood",
            "Block_processing_delay_under_admission_flood_groth16",
            "Block_processing_delay_under_admission_flood_signature_stuffed",
            "Sustainable_rejection_rate_by_ramp",
            "Sustainable_rejection_rate_by_ramp_groth16",
            "Sustainable_rejection_rate_by_ramp_signature_stuffed",
            "Sustainable_rejection_rate_by_ramp_with_retries",
        ],
        combos=(
            [("Admission_flood_actually_reaches_the_simulator", shape)
             for shape in ["keccak-wide", *GROTH16_SHAPES, "signature-stuffed"]]
            + [("Block_production_delay_under_flood_and_retries", c, k, r)
               for c in CEILINGS for k in (1, 8) for r in (0, 100)]
            + [("Block_processing_delay_under_admission_flood", c, r) for c in CEILINGS for r in (50, 200)]
            + [("Block_processing_delay_under_admission_flood_groth16", shape, r)
               for shape in GROTH16_SHAPES for r in (50, 200)]
            + [("Block_processing_delay_under_admission_flood_signature_stuffed", c, r)
               for c in CEILINGS for r in (50, 200)]
            + [("Sustainable_rejection_rate_by_ramp", c) for c in CEILINGS]
            + [("Sustainable_rejection_rate_by_ramp_groth16", shape) for shape in GROTH16_SHAPES]
            + [("Sustainable_rejection_rate_by_ramp_signature_stuffed", c) for c in CEILINGS]
            + [("Sustainable_rejection_rate_by_ramp_with_retries", c, k)
               for c in CEILINGS for k in (K_RETRIES if c == 100_000 else (1, 8))]
        ),
        single_core=True,
        needs_groth16=True,
        max_ceiling=510_000,
        result_env="FRAME_FLOOD_OUT",
    ),
    Fixture(
        name="FrameTxProducerRetryMeasurement",
        harness="producer-retry",
        project="Nethermind.Blockchain.Test",
        methods=["ProducerRetriesAFailingPrefix", "ProducerRetriesAreBoundedByKRetry"],
        combos=(
            [("ProducerRetriesAFailingPrefix", True, 236_285),
             ("ProducerRetriesAFailingPrefix", False, 300_000),
             ("ProducerRetriesAFailingPrefix", False, 236_285)]
            + [("ProducerRetriesAreBoundedByKRetry", c, k) for c in CEILINGS for k in K_RETRIES]
        ),
        max_ceiling=500_000,  # never clamped: drives Execute() directly, not the admission-time cap
        result_env="FRAME_RETRY_OUT",
    ),
    Fixture(
        name="FrameTxPrefixRetryMeasurement",
        harness="prefix-retry",
        project="Nethermind.TxPool.Test",
        methods=[
            "Control_included_frame_transaction_leaves_the_pool",
            "Unincludable_frame_transaction_survives_every_head",
            "Expiry_deadline_bounds_the_number_of_attempts",
        ],
        combos=[
            ("Control_included_frame_transaction_leaves_the_pool",),
            ("Unincludable_frame_transaction_survives_every_head",),
            ("Expiry_deadline_bounds_the_number_of_attempts",),
        ],
        result_env="FRAME_RETRY_OUT",
    ),
]

FIXTURES_BY_NAME = {f.name: f for f in FIXTURES}
HARNESSES = sorted({f.harness for f in FIXTURES})


def select_fixtures(harness: str | None, fixture: str | None, ceiling: int | None) -> list[Fixture]:
    chosen = FIXTURES
    if fixture:
        if fixture not in FIXTURES_BY_NAME:
            raise SystemExit(f"unknown fixture '{fixture}'. Known: {', '.join(FIXTURES_BY_NAME)}")
        chosen = [FIXTURES_BY_NAME[fixture]]
    elif harness:
        if harness not in HARNESSES:
            raise SystemExit(f"unknown harness '{harness}'. Known: {', '.join(HARNESSES)}")
        chosen = [f for f in FIXTURES if f.harness == harness]
    if ceiling is not None:
        chosen = [f for f in chosen if f.max_ceiling >= ceiling]
    return chosen


# --------------------------------------------------------------------------------------
# list
# --------------------------------------------------------------------------------------

def cmd_list(args: argparse.Namespace) -> int:
    fixtures = select_fixtures(args.harness, args.fixture, None)
    if not fixtures:
        print("no fixtures match that filter", file=sys.stderr)
        return 1

    if args.fixture:
        fx = fixtures[0]
        print(f"{fx.name}  ({fx.harness}, {fx.project})")
        print(f"  single_core={fx.single_core}  needs_groth16_artifacts={fx.needs_groth16}  "
              f"max_ceiling={fx.max_ceiling}")
        print(f"  {len(fx.combos)} input combinations:")
        for combo in fx.combos:
            method, *params = combo
            print(f"    {method}({', '.join(str(p) for p in params)})")
        return 0

    print(f"{'fixture':42} {'harness':16} {'project':24} {'combos':7} {'core':6} {'groth16':8}")
    for fx in fixtures:
        print(f"{fx.name:42} {fx.harness:16} {fx.project:24} {len(fx.combos):<7} "
              f"{'1':6} {'yes' if fx.needs_groth16 else 'no':8}" if fx.single_core else
              f"{fx.name:42} {fx.harness:16} {fx.project:24} {len(fx.combos):<7} "
              f"{'any':6} {'yes' if fx.needs_groth16 else 'no':8}")
    print(f"\n{sum(len(f.combos) for f in fixtures)} total input combinations across "
          f"{len(fixtures)} fixture(s). Use `list --fixture NAME` to see them.")
    return 0


# --------------------------------------------------------------------------------------
# run
# --------------------------------------------------------------------------------------

def sh(cmd: list[str], cwd: Path, env: dict, dry_run: bool, tee: Path | None = None) -> int:
    print(f"+ {' '.join(cmd)}", file=sys.stderr)
    if dry_run:
        return 0
    if tee is None:
        return subprocess.run(cmd, cwd=cwd, env=env).returncode
    with open(tee, "a") as handle:
        proc = subprocess.Popen(cmd, cwd=cwd, env=env, stdout=subprocess.PIPE,
                                 stderr=subprocess.STDOUT, text=True)
        for line in proc.stdout:  # type: ignore[union-attr]
            sys.stdout.write(line)
            handle.write(line)
        return proc.wait()


def patch_verify_gas_const(value: int) -> str:
    """Sed-patch Eip8141Constants.MaxVerifyGas, matching the CI workflow's own safety checks.
    Returns the original file content so the caller can restore it."""
    path = SRC_DIR / CONST_FILE
    original = path.read_text()
    hits = original.count(STOCK_LITERAL)
    if hits != 1:
        raise SystemExit(f"expected exactly one '{STOCK_LITERAL}' in {CONST_FILE}, found {hits}. "
                          "Refusing to patch -- the constant may have moved.")
    path.write_text(original.replace(STOCK_LITERAL, f"MaxVerifyGas = {value}"))
    return original


def cmd_run(args: argparse.Namespace) -> int:
    fixtures = select_fixtures(args.harness, args.fixture, args.ceiling)
    if not fixtures:
        print("no fixtures match that filter", file=sys.stderr)
        return 1

    out_dir = Path(args.out_dir) if args.out_dir else (
        Path(__file__).parent / "out" / datetime.now(timezone.utc).strftime("%Y-%m-%dT%H%M%SZ"))
    out_dir.mkdir(parents=True, exist_ok=True)
    print(f"output directory: {out_dir}")

    if args.raise_verify_gas and args.raise_verify_gas < 500_000:
        raise SystemExit("--raise-verify-gas unlocks no sweep point below 500000 (both harnesses "
                          "skip a ceiling strictly greater than the constant); use 510000 for the "
                          "complete sweep including the 49-input Groth16 point")

    needs_groth16 = any(f.needs_groth16 for f in fixtures)
    if needs_groth16 and not args.groth16_artifacts:
        print("warning: no --groth16-artifacts given; privacy-workload cases will self-ignore",
              file=sys.stderr)

    if any(f.single_core for f in fixtures) and not args.allow_multicore \
            and shutil.which("taskset") is None:
        raise SystemExit("taskset is not available; FrameTxFloodMeasurement needs single-core "
                          "pinning to measure real contention. Pass --allow-multicore to override.")

    original_const: str | None = None
    if args.raise_verify_gas and args.dry_run:
        print(f"+ (dry-run) would patch {CONST_FILE}: {STOCK_LITERAL} -> "
              f"MaxVerifyGas = {args.raise_verify_gas}")
    elif args.raise_verify_gas:
        original_const = patch_verify_gas_const(args.raise_verify_gas)
        (out_dir / "PROVENANCE.txt").write_text(
            f"eip8141_max_verify_gas_patched_from=300000 to={args.raise_verify_gas}\n"
            f"# every RESULT row in this artifact came from a build carrying this patch\n")
        print(f"::warning:: MaxVerifyGas patched 300000 -> {args.raise_verify_gas} for this run")

    status = 0
    try:
        built: set[str] = set()
        for fx in fixtures:
            if fx.project not in built and not args.no_build:
                rc = sh(["dotnet", "build", f"{fx.project}/{fx.project}.csproj", "-c", "release"],
                        cwd=SRC_DIR, env=os.environ.copy(), dry_run=args.dry_run)
                if rc != 0:
                    return rc
                built.add(fx.project)

        for fx in fixtures:
            results = out_dir / f"{fx.name}-results.txt"
            env = os.environ.copy()
            env["FRAME_MEMPOOL_DOS_OUT"] = str(results)
            env["FRAME_RETRY_OUT"] = str(results)
            env["FRAME_FLOOD_OUT"] = str(results)
            if args.groth16_artifacts:
                env["FRAME_GROTH16_ARTIFACTS"] = args.groth16_artifacts

            cmd = ["dotnet", "test", "--project", f"{fx.project}/{fx.project}.csproj", "-c", "release",
                   "--no-build", "--no-restore", "--", "--no-ansi", "--output", "Detailed",
                   "--filter", f"FullyQualifiedName~{fx.name}"]
            if fx.single_core and not args.allow_multicore:
                cmd = ["taskset", "-c", "0", *cmd]

            print(f"\n=== {fx.name} -> {results} ===")
            rc = sh(cmd, cwd=SRC_DIR, env=env, dry_run=args.dry_run, tee=out_dir / f"{fx.name}.log")
            if rc != 0:
                print(f"::error:: {fx.name} exited {rc}", file=sys.stderr)
                status = 1
            if not args.dry_run and not results.exists():
                print(f"::error:: {fx.name} produced no RESULT lines at {results}", file=sys.stderr)
                status = 1
    finally:
        if original_const is not None and not args.dry_run:
            (SRC_DIR / CONST_FILE).write_text(original_const)
            print(f"reverted {CONST_FILE}")

    print(f"\nrun complete, status={status}. Parse it with:\n"
          f"  ./frame_tx_bench.py report --in {out_dir}/*-results.txt --summary")
    return status


# --------------------------------------------------------------------------------------
# report: parse RESULT rows -> tables + charts
# --------------------------------------------------------------------------------------

ROW_RE = re.compile(r"^RESULT\s+(.*)$")
KV_RE = re.compile(r"(\S+?)=(\S+)")


def parse_rows(paths: list[str]) -> list[dict]:
    rows = []
    for pattern in paths:
        for path in sorted(glob.glob(pattern)) or [pattern]:
            if not os.path.isfile(path):
                continue
            with open(path, errors="replace") as handle:
                for line in handle:
                    m = ROW_RE.match(line.strip())
                    if not m:
                        continue
                    kv = dict(KV_RE.findall(m.group(1)))
                    kv["_source"] = os.path.basename(path)
                    rows.append(kv)
    return rows


def numeric(value: str):
    try:
        return int(value)
    except ValueError:
        pass
    try:
        return float(value)
    except ValueError:
        return value


def print_table(rows: list[dict], columns: list[str], title: str) -> None:
    if not rows:
        print(f"\n## {title}\n  (no rows)")
        return
    print(f"\n## {title}  ({len(rows)} rows)")
    widths = {c: max(len(c), *(len(str(r.get(c, ""))) for r in rows)) for c in columns}
    print("  " + "  ".join(c.ljust(widths[c]) for c in columns))
    print("  " + "  ".join("-" * widths[c] for c in columns))
    for r in rows:
        print("  " + "  ".join(str(r.get(c, "")).ljust(widths[c]) for c in columns))


def write_csv(rows: list[dict], columns: list[str], path: Path) -> None:
    import csv
    with open(path, "w", newline="") as handle:
        writer = csv.DictWriter(handle, fieldnames=columns, extrasaction="ignore")
        writer.writeheader()
        writer.writerows(rows)


def ascii_bar(pairs: list[tuple[str, float]], title: str, width: int = 50) -> None:
    print(f"\n## {title} (ASCII; install matplotlib for PNG charts)")
    if not pairs:
        print("  (no data)")
        return
    top = max(v for _, v in pairs) or 1
    for label, value in pairs:
        bar = "#" * max(1, int(width * value / top)) if value else ""
        print(f"  {label:>18} | {bar} {value:g}")


def try_png_chart(series: dict[str, list[tuple[float, float]]], title: str, xlabel: str, ylabel: str,
                   out_path: Path) -> bool:
    try:
        import matplotlib
        matplotlib.use("Agg")
        import matplotlib.pyplot as plt
    except ImportError:
        return False
    fig, ax = plt.subplots(figsize=(8, 5))
    for label, points in series.items():
        points = sorted(points)
        ax.plot([p[0] for p in points], [p[1] for p in points], marker="o", label=label)
    ax.set_title(title)
    ax.set_xlabel(xlabel)
    ax.set_ylabel(ylabel)
    ax.legend(fontsize="small")
    ax.grid(True, alpha=0.3)
    fig.tight_layout()
    fig.savefig(out_path, dpi=140)
    plt.close(fig)
    return True


def group_by_case(rows: list[dict]) -> dict[str, list[dict]]:
    out: dict[str, list[dict]] = defaultdict(list)
    for r in rows:
        out[r.get("case", "?")].append(r)
    return out


def cmd_report(args: argparse.Namespace) -> int:
    rows = parse_rows(args.paths)
    if not rows:
        print("no RESULT rows found in the given paths", file=sys.stderr)
        return 1

    by_case = group_by_case(rows)
    out_dir = Path(args.out_dir) if args.out_dir else Path(".")
    out_dir.mkdir(parents=True, exist_ok=True)

    if args.case:
        cases = [args.case] if args.case in by_case else []
        if not cases:
            print(f"no rows with case={args.case}; available: {', '.join(sorted(by_case))}",
                  file=sys.stderr)
            return 1
    else:
        cases = sorted(by_case)

    for case in cases:
        case_rows = by_case[case]
        columns = []
        for r in case_rows:
            for k in r:
                if k not in columns and k != "_source":
                    columns.append(k)
        print_table(case_rows, columns, f"case={case}")
        if args.csv:
            path = out_dir / f"{case}.csv"
            write_csv(case_rows, columns, path)
            print(f"  wrote {path}")

    if args.summary:
        print_plan_summary(by_case, out_dir, args)

    return 0


def print_plan_summary(by_case: dict[str, list[dict]], out_dir: Path, args: argparse.Namespace) -> None:
    """The plan's own metric table: t_reject, W_0/W/Delta, R_max, and (if -B/-r_attack given) C*."""
    print("\n" + "=" * 78)
    print("PLAN SUMMARY  (Benchmarking VERIFY frame gas ceiling)")
    print("=" * 78)

    # t_reject(C): admission reject latency, p50/p99, per shape+ceiling.
    treject_rows = []
    for case in ("frame_reject", "signature_reject", "ecrecover_baseline"):
        for r in by_case.get(case, []):
            treject_rows.append({
                "shape": r.get("shape", "?"),
                "ceiling": r.get("verify_gas") or r.get("ceiling") or "-",
                "t_reject_p50_us": r.get("submit_p50_us", "-"),
                "t_reject_p99_us": r.get("submit_p99_us", "-"),
                "samples": r.get("samples", "-"),
            })
    print_table(treject_rows, ["shape", "ceiling", "t_reject_p50_us", "t_reject_p99_us", "samples"],
                "t_reject(C) -- admission reject latency")

    # W_0 / W(C,r,K_retry) / Delta: from flood_delay and production_under_flood rows.
    delta_rows = []
    delta_series: dict[str, list[tuple[float, float]]] = defaultdict(list)
    for r in by_case.get("flood_delay", []):
        shape, ceiling = r.get("shape", "?"), numeric(r.get("ceiling", 0))
        delta_rows.append({
            "shape": shape, "ceiling": ceiling, "k_retry": "-",
            "offered_rate": r.get("offered_rate"), "achieved_rate": r.get("achieved_rate"),
            "W0_p50_us": r.get("W0_p50_us"), "W_p50_us": r.get("W_p50_us"),
            "delta_p50_us": r.get("delta_p50_us"), "delta_p50_pct": r.get("delta_p50_pct"),
            "saturated": r.get("saturated"),
        })
        if isinstance(ceiling, (int, float)) and r.get("delta_p50_us") not in (None, ""):
            delta_series[f"{shape} r={r.get('offered_rate')}"].append((ceiling, numeric(r["delta_p50_us"])))
    for r in by_case.get("production_under_flood", []):
        if r.get("offered_rate") == "0":
            continue
        delta_rows.append({
            "shape": "keccak-wide", "ceiling": r.get("ceiling"), "k_retry": r.get("k_retry"),
            "offered_rate": r.get("offered_rate"), "achieved_rate": r.get("flood_achieved_rate", "-"),
            "W0_p50_us": "-", "W_p50_us": r.get("production_p50_us"),
            "delta_p50_us": "-", "delta_p50_pct": "-", "saturated": r.get("flood_starved", "-"),
        })
    print_table(delta_rows, ["shape", "ceiling", "k_retry", "offered_rate", "achieved_rate",
                              "W0_p50_us", "W_p50_us", "delta_p50_us", "delta_p50_pct", "saturated"],
                "W_0 / W(C,r,K_retry) / Delta -- block-processing delay under flood")

    # R_max(C, K_retry): sustainable reject rate.
    rmax_rows = []
    rmax_series: dict[str, list[tuple[float, float]]] = defaultdict(list)
    for case in ("r_max", "r_max_with_retries"):
        for r in by_case.get(case, []):
            k_retry = r.get("k_retry", "1")
            shape, ceiling = r.get("shape", "keccak-wide"), numeric(r.get("ceiling", 0))
            rate = r.get("r_max_sustained_tx_per_s")
            rmax_rows.append({"shape": shape, "ceiling": ceiling, "k_retry": k_retry,
                               "r_max_tx_per_s": rate, "censored": r.get("censored")})
            if isinstance(ceiling, (int, float)) and rate not in (None, ""):
                rmax_series[f"{shape} K={k_retry}"].append((ceiling, numeric(rate)))
    print_table(rmax_rows, ["shape", "ceiling", "k_retry", "r_max_tx_per_s", "censored"],
                "R_max(C, K_retry) -- sustainable reject rate")

    # Charts.
    if delta_series:
        if not try_png_chart(delta_series, "Block-processing delay vs ceiling", "ceiling (gas)",
                              "delta p50 (us)", out_dir / "delta_vs_ceiling.png"):
            ascii_bar([(k, v[-1][1]) for k, v in delta_series.items()],
                      "delta_p50_us by shape/rate (last ceiling point)")
        else:
            print(f"\nwrote {out_dir / 'delta_vs_ceiling.png'}")
    if rmax_series:
        if not try_png_chart(rmax_series, "Sustainable reject rate vs ceiling", "ceiling (gas)",
                              "R_max (tx/s)", out_dir / "rmax_vs_ceiling.png"):
            ascii_bar([(k, v[-1][1]) for k, v in rmax_series.items()],
                      "R_max by shape/K_retry (last ceiling point)")
        else:
            print(f"\nwrote {out_dir / 'rmax_vs_ceiling.png'}")

    # Decision rule: C* = largest ceiling >= 236k with Delta(C, r_attack, K_retry) <= B.
    if args.budget_us is not None and args.attack_rate is not None:
        print(f"\n## Decision rule  (B={args.budget_us}us, r_attack={args.attack_rate} tx/s)")
        by_shape_rate: dict[tuple, list[dict]] = defaultdict(list)
        for r in delta_rows:
            if r["delta_p50_us"] in ("-", None):
                continue
            by_shape_rate[(r["shape"], r["k_retry"])].append(r)
        if not by_shape_rate:
            print("  no Delta rows with both a shape and an offered_rate to evaluate")
        for (shape, k_retry), candidates in sorted(by_shape_rate.items()):
            nearest_rate = min({numeric(c["offered_rate"]) for c in candidates},
                                key=lambda r: abs(r - args.attack_rate))
            at_rate = [c for c in candidates if numeric(c["offered_rate"]) == nearest_rate]
            passing = [c for c in at_rate
                       if numeric(c["ceiling"]) >= 236_000 and numeric(c["delta_p50_us"]) <= args.budget_us]
            c_star = max((numeric(c["ceiling"]) for c in passing), default=None)
            note = "" if nearest_rate == args.attack_rate else f"  (nearest measured rate: {nearest_rate})"
            print(f"  shape={shape} k_retry={k_retry}: C* = {c_star if c_star else 'none -- no ceiling >= 236k satisfies B'}{note}")


# --------------------------------------------------------------------------------------

def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="command", required=True)

    pl = sub.add_parser("list", help="list fixtures and their input combinations")
    pl.add_argument("--harness", choices=HARNESSES)
    pl.add_argument("--fixture", choices=list(FIXTURES_BY_NAME))
    pl.set_defaults(func=cmd_list)

    pr = sub.add_parser("run", help="build and run fixtures with dotnet test")
    pr.add_argument("--harness", choices=HARNESSES)
    pr.add_argument("--fixture", choices=list(FIXTURES_BY_NAME))
    pr.add_argument("--ceiling", type=int, help="only run fixtures whose sweep reaches this ceiling")
    pr.add_argument("--groth16-artifacts", help="path for FRAME_GROTH16_ARTIFACTS")
    pr.add_argument("--raise-verify-gas", type=int,
                     help="sed-patch Eip8141Constants.MaxVerifyGas before building (e.g. 510000), "
                          "reverted after the run -- mirrors the CI workflow's raise_verify_gas_const")
    pr.add_argument("--out-dir", help="default: tools/frame-tx-bench/out/<UTC timestamp>/")
    pr.add_argument("--no-build", action="store_true", help="skip `dotnet build`, reuse the last build")
    pr.add_argument("--allow-multicore", action="store_true",
                     help="skip taskset -c 0 pinning for FrameTxFloodMeasurement (weakens the result)")
    pr.add_argument("--dry-run", action="store_true", help="print commands without running them")
    pr.set_defaults(func=cmd_run)

    rp = sub.add_parser("report", help="parse RESULT rows into tables, a plan summary, and charts")
    rp.add_argument("--in", dest="paths", nargs="+", required=True,
                     help="result files or globs, e.g. out/2026-.../*-results.txt")
    rp.add_argument("--case", help="only show this case= (default: all cases found)")
    rp.add_argument("--summary", action="store_true",
                     help="also print the plan-shaped summary (t_reject, Delta, R_max, C*)")
    rp.add_argument("--budget-us", type=float, help="B: acceptable block-processing delay increase, in us")
    rp.add_argument("--attack-rate", type=float, help="r_attack: target adversarial tx/s for C*")
    rp.add_argument("--csv", action="store_true", help="also write one CSV per case to --out-dir")
    rp.add_argument("--out-dir", help="where to write CSVs/PNGs (default: current directory)")
    rp.set_defaults(func=cmd_report)

    return p


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    raise SystemExit(main())
