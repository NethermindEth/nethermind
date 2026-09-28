"""CLI entry point for the frame-transaction traffic generator."""
from __future__ import annotations

import argparse
import os
import platform
import sys
import threading
import time

from . import roles as roles_mod
from . import runner, shapes
from . import accounts as accounts_mod
from .accounts import AccountPool, parse_accounts
from .results import Recorder
from .rpc import RpcClient


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(prog="frame_traffic", description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)

    run = sub.add_parser("run", help="run one scenario against a live devnet")
    run.add_argument("--scenario-id", required=True)
    run.add_argument("--chain-id", type=int, required=True)
    run.add_argument("--ceiling", type=int, required=True, help="MAX_VERIFY_GAS under test")
    run.add_argument("--k-retry", type=int, default=1)
    run.add_argument("--seconds-per-slot", type=int, default=6)
    run.add_argument("--rpc", action="append", required=True,
                     metavar="NAME=CLIENT=URL", help="repeat once per execution node")
    run.add_argument("--account", action="append", required=True,
                     metavar="ADDRESS:PRIVATE_KEY", help="repeat; one per role is needed")
    run.add_argument("--attacker-role", default="keccak-wide",
                     choices=sorted(roles_mod.ATTACKER_ROLES) + ["none"])
    run.add_argument("--attacker-rate", type=float, default=25.0)
    run.add_argument("--baseline-rate", type=float, default=2.0)
    run.add_argument("--duration", type=float, default=240.0)
    run.add_argument("--warmup", type=float, default=30.0)
    run.add_argument("--results-dir", default="/results")
    run.add_argument("--groth16-artifacts", default="")
    run.add_argument("--privacy-inclusion", action="store_true")
    run.add_argument("--privacy-valid-calldata", default="",
                     help="file holding real valid shielded-pool calldata; without it the "
                          "privacy probe reports itself unavailable instead of faking a proof")
    run.add_argument("--privacy-attempts", type=int, default=3)
    run.add_argument("--network-timeout", type=float, default=300.0)
    run.add_argument("--fork-timeout", type=float, default=600.0,
                     help="how long to wait for frame transactions to become valid "
                          "(the devnet schedules Hegota at epoch 1)")
    run.add_argument("--settle-slots", type=int, default=10,
                     help="slots to keep watching the chain after the load stops, so a late "
                          "admission is judged on the chain rather than on the clock")
    run.add_argument("--split-traffic", action="store_true",
                     help="aim the attack at the first node and the honest traffic at the second, "
                          "to separate damage local to the attacked node from damage to the chain")
    run.add_argument("--keep-alive", action="store_true",
                     help="park after the run so the enclave keeps the service up for collection")
    return parser.parse_args(argv)


def build_nodes(specs: list[str]) -> list[RpcClient]:
    nodes = []
    for spec in specs:
        parts = spec.split("=", 2)
        if len(parts) != 3:
            raise SystemExit("--rpc '{0}' is not NAME=CLIENT=URL".format(spec))
        nodes.append(RpcClient(parts[0], parts[1], parts[2]))
    return nodes


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv if argv is not None else sys.argv[1:])
    nodes = build_nodes(args.rpc)
    recorder = Recorder(args.results_dir, args.scenario_id)

    module = shapes.load_encoder()
    pool = AccountPool(parse_accounts(args.account))

    ctx = roles_mod.Context(
        module=module,
        chain_id=args.chain_id,
        ceiling=args.ceiling,
        k_retry=args.k_retry,
        seconds_per_slot=args.seconds_per_slot,
        nodes=nodes,
        recorder=recorder,
        groth16_artifacts=args.groth16_artifacts,
        privacy_calldata_path=args.privacy_valid_calldata,
    )

    recorder.emit(
        "scenario",
        ceiling=args.ceiling,
        k_retry=args.k_retry,
        attacker_role=args.attacker_role,
        attacker_rate=args.attacker_rate,
        baseline_rate=args.baseline_rate,
        warmup_s=args.warmup,
        duration_s=args.duration,
        seconds_per_slot=args.seconds_per_slot,
        nodes=len(nodes),
        chain_id=args.chain_id,
    )

    if not runner.wait_for_network(ctx, args.network_timeout):
        recorder.emit("scenario_aborted", reason="network did not become ready")
        recorder.close()
        return 1

    attacker_sender = pool.assign("attacker")
    probe_sender = pool.assign("privacy") if args.privacy_inclusion else None
    fork_gate_sender = pool.assign("fork-gate")
    ceiling_probe_sender = pool.assign("ceiling-probe")
    # Every remaining account carries honest traffic, one transaction in flight each.
    baseline_senders = accounts_mod.SenderRotation(pool.assign_rest("baseline"))
    attacker_nonces = accounts_mod.FixedNonce(attacker_sender)

    if not runner.wait_for_fork_activation(ctx, fork_gate_sender, args.fork_timeout):
        recorder.emit("scenario_aborted", reason="frame transactions never became valid; "
                                                 "check the heze fork schedule")
        recorder.close()
        return 1

    runner.verify_ceiling_is_active(ctx, ceiling_probe_sender)

    baseline_role = roles_mod.BaselineRole()
    attacker_role = None
    if args.attacker_role != "none":
        attacker_role = roles_mod.ATTACKER_ROLES[args.attacker_role]()
        try:
            attacker_role.prepare(ctx, attacker_sender)
        except Exception as error:
            recorder.emit("scenario_aborted", reason=str(error), role=args.attacker_role)
            recorder.close()
            return 1

    attack_nodes = honest_nodes = None
    if args.split_traffic and len(nodes) >= 2:
        attack_nodes, honest_nodes = [nodes[0]], [nodes[1]]
        recorder.emit("traffic_split", attacked=nodes[0].name, observed=nodes[1].name)

    # Before any load: prove this generator can build what it is about to offer. A shape that
    # costs more to build than the offered rate allows turns the generator into the bottleneck,
    # and for the signature-stuffed shape that cost scales with the ceiling under test, so the
    # limit would move with the independent variable and read as a result.
    for role, rate, account in ((baseline_role, args.baseline_rate, baseline_senders.senders[0]),
                                (attacker_role, args.attacker_rate, attacker_sender)):
        if role is None:
            continue
        if not runner.calibrate_generator(ctx, role, account, rate, recorder):
            recorder.emit("scenario_aborted",
                          reason="generator cannot build the offered rate for this shape; "
                                 "see case=generator_capacity",
                          role=role.name)
            recorder.close()
            return 1

    head_tracker = runner.HeadTracker(nodes, recorder)
    head_tracker.start()

    # Follows the node the honest traffic is aimed at, so inclusion is read from the same
    # vantage point the inclusion claim is about. Blocks are shared, so the choice only
    # affects whose RPC serves the walk.
    watcher = runner.ChainWatcher(honest_nodes[0] if honest_nodes else nodes[0], recorder)
    watcher.start()

    # Warm-up: baseline only, so the attacker's effect is measured against a live chain that
    # is already carrying valid frame transactions rather than an idle one.
    warm = runner.Submitter(baseline_role, baseline_senders, ctx, args.baseline_rate, recorder,
                            track_inclusion=True, nodes=honest_nodes, watcher=watcher)
    warm.start()
    time.sleep(args.warmup)
    warm.stop()
    # The last warm-up transactions are still in flight; give them the slots they need before
    # reading the count, or the warm-up baseline is understated for the same reason the
    # measured window used to be.
    watcher.settle(args.seconds_per_slot * 2)
    warm.report("warmup")

    baseline = runner.Submitter(baseline_role, baseline_senders, ctx, args.baseline_rate, recorder,
                                track_inclusion=True, nodes=honest_nodes, watcher=watcher)
    attacker = None
    if attacker_role is not None:
        attacker = runner.Submitter(attacker_role, attacker_nonces, ctx, args.attacker_rate, recorder,
                                    nodes=attack_nodes)

    baseline.start()
    if attacker is not None:
        attacker.start()

    probe_thread = None
    if args.privacy_inclusion and probe_sender is not None:
        probe = roles_mod.PrivacyInclusionProbe(args.privacy_valid_calldata)
        # Runs inside the measured window on purpose: the question is whether a valid privacy
        # transaction still lands while the attack is in progress.
        probe_thread = threading.Thread(
            target=probe.run,
            args=(ctx, probe_sender, args.privacy_attempts, max(args.duration / (args.privacy_attempts + 1), 1.0)),
            name="privacy-probe",
            daemon=True,
        )
        probe_thread.start()

    time.sleep(args.duration)

    if attacker is not None:
        attacker.stop()
    baseline.stop()
    if probe_thread is not None:
        probe_thread.join(timeout=args.seconds_per_slot * 10)

    # Inclusion is a property of the chain, not of the load: a transaction admitted in the last
    # second of the window is only fairly judged after the chain has had room to include it.
    settle_seconds = args.settle_slots * args.seconds_per_slot
    recorder.emit("settling", seconds=settle_seconds, slots=args.settle_slots)
    watcher.settle(settle_seconds)
    watcher.stop()

    baseline.report("measured")
    if attacker is not None:
        attacker.report("measured")

    watcher.report_blocks()
    head_tracker.stop()
    head_tracker.report()

    metadata_path = recorder.write_metadata(
        {
            "scenario_id": args.scenario_id,
            "ceiling": args.ceiling,
            "k_retry": args.k_retry,
            "attacker_role": args.attacker_role,
            "attacker_rate": args.attacker_rate,
            "baseline_rate": args.baseline_rate,
            "warmup_seconds": args.warmup,
            "duration_seconds": args.duration,
            "seconds_per_slot": args.seconds_per_slot,
            "chain_id": args.chain_id,
            "nodes": [{"name": n.name, "client": n.client, "url": n.url} for n in nodes],
            "accounts": {role: ([s.address for s in held] if isinstance(held, list) else held.address)
                         for role, held in pool.assigned().items()},
            "fixtures": {
                key: ("0x" + value.hex() if isinstance(value, (bytes, bytearray)) else value)
                for key, value in ctx.fixtures.items()
                if not isinstance(value, (bytes, bytearray)) or len(value) == 20
            },
            "groth16_artifacts": args.groth16_artifacts,
            "privacy_valid_calldata": args.privacy_valid_calldata,
            "generator": {
                "python": platform.python_version(),
                "encoder": getattr(module, "__file__", "unknown"),
            },
            "finished_at": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        }
    )
    recorder.emit("scenario_complete", metadata=os.path.basename(metadata_path))
    recorder.close()

    if args.keep_alive:
        print("run complete; parking so the enclave keeps this service up for collection",
              flush=True)
        while True:
            time.sleep(3600)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
