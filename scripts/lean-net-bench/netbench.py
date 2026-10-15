#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
"""EIP-8437 lean/1 network benchmark controller: RLPx (TCP) against ethp2p (QUIC) over emulated links.

Each node is a separate process running LeanNetBenchNode (Nethermind.Network.Test). On Linux with --netns, every node
gets its own network namespace attached by a veth pair to a bridge in a router namespace. Links are shaped with tc:
the node side caps the uplink (tbf), the router side caps the downlink (htb) and applies netem delay and loss per
source, so each topology edge has the configured RTT and loss. Without --netns the nodes share loopback (no shaping).

Usage: netbench.py run --topology chain4 --binding ethp2p --broadcast --kind 2 --proof-bytes 300000 --rtt-ms 50 --loss 1
       netbench.py teardown
"""
import argparse
import json
import math
import os
import hashlib
import platform
import re
import statistics
import subprocess
import sys
import time
import traceback

PREFIX = os.environ.get("LEAN_NET_PREFIX", "lb")
ROUTER = f"{PREFIX}-r"
# Node i is NET.(i+1).1/16: one bridge, but a distinct /24 per node, so the ethp2p per-subnet inbound limit
# (four per /24 for public addresses, per address for private ones) never groups benchmark peers.
NET = os.environ.get("LEAN_NET_SUBNET", "10.77")
RLPX_PORT = 30303
QUIC_PORT = 30304


def sh(cmd, check=True):
    result = subprocess.run(cmd, shell=True, capture_output=True, text=True)
    if check and result.returncode != 0:
        raise RuntimeError(f"{cmd}: {result.stderr.strip()}")
    return result.stdout


def topology(name):
    """Returns (node count, undirected edges)."""
    if name == "pair":
        return 2, [(0, 1)]
    if name.startswith("chain"):
        n = int(name[5:])
        return n, [(i, i + 1) for i in range(n - 1)]
    if name == "cube8":
        return 8, sorted({tuple(sorted((i, i ^ b))) for i in range(8) for b in (1, 2, 4)})
    if name == "star4":
        return 4, [(0, i) for i in range(1, 4)]
    raise ValueError(name)


def distances(n, edges, source):
    adjacency = {i: set() for i in range(n)}
    for a, b in edges:
        adjacency[a].add(b)
        adjacency[b].add(a)
    dist = {source: 0}
    frontier = [source]
    while frontier:
        nxt = []
        for u in frontier:
            for v in adjacency[u]:
                if v not in dist:
                    dist[v] = dist[u] + 1
                    nxt.append(v)
        frontier = nxt
    return dist


def ns(i):
    return f"{PREFIX}-n{i}"


def ip(i):
    return f"{NET}.{i + 1}.1"


def teardown():
    for name in sh("ip netns list", check=False).split("\n"):
        name = name.split(" ")[0]
        if name.startswith(f"{PREFIX}-"):
            sh(f"ip netns del {name}", check=False)


def setup(n):
    teardown()
    sh(f"ip netns add {ROUTER}")
    sh(f"ip -n {ROUTER} link add br0 type bridge")
    sh(f"ip -n {ROUTER} link set br0 up")
    sh(f"ip -n {ROUTER} link set lo up")
    for i in range(n):
        sh(f"ip netns add {ns(i)}")
        sh(f"ip -n {ns(i)} link set lo up")
        sh(f"ip -n {ns(i)} link add eth0 type veth peer name r{i} netns {ROUTER}")
        sh(f"ip -n {ns(i)} addr add {ip(i)}/16 dev eth0")
        sh(f"ip -n {ns(i)} link set eth0 up")
        sh(f"ip -n {ROUTER} link set r{i} master br0")
        sh(f"ip -n {ROUTER} link set r{i} up")
        # Segmentation offloads would let tc see 64 KiB super-packets; netem must drop and delay real MTU packets.
        sh(f"ip netns exec {ns(i)} ethtool -K eth0 tso off gso off gro off", check=False)
        sh(f"ip netns exec {ROUTER} ethtool -K r{i} tso off gso off gro off", check=False)


def shape(n, edges, rtt_ms, loss, rate_mbit):
    one_way = rtt_ms / 2.0
    neighbours = {i: set() for i in range(n)}
    for a, b in edges:
        neighbours[a].add(b)
        neighbours[b].add(a)
    for j in range(n):
        if rate_mbit:
            # Uplink: the node's transmit queue is about 1 MiB, enough for one bandwidth-delay product at 100 Mbit/s and 80 ms.
            sh(f"ip netns exec {ns(j)} tc qdisc replace dev eth0 root tbf rate {rate_mbit}mbit burst 32kb limit 1048576")
        else:
            sh(f"ip netns exec {ns(j)} tc qdisc del dev eth0 root", check=False)
        dev = f"r{j}"
        sh(f"ip netns exec {ROUTER} tc qdisc del dev {dev} root", check=False)
        rate = f"{rate_mbit}mbit" if rate_mbit else "10gbit"
        sh(f"ip netns exec {ROUTER} tc qdisc add dev {dev} root handle 1: htb default 1")
        sh(f"ip netns exec {ROUTER} tc class add dev {dev} parent 1: classid 1:1 htb rate {rate} ceil {rate}")
        for i in sorted(neighbours[j]):
            cls = 10 + i
            sh(f"ip netns exec {ROUTER} tc class add dev {dev} parent 1:1 classid 1:{cls} htb rate {rate} ceil {rate}")
            netem = f"delay {one_way}ms" if one_way > 0 else "delay 0ms"
            if loss > 0:
                netem += f" loss {loss}%"
            sh(f"ip netns exec {ROUTER} tc qdisc add dev {dev} parent 1:{cls} handle {cls}: netem {netem} limit 100000")
            sh(f"ip netns exec {ROUTER} tc filter add dev {dev} parent 1: protocol ip prio 1 u32 match ip src {ip(i)}/32 flowid 1:{cls}")


def counters(i):
    """Interface bytes and packets and the namespace's UDP and TCP error counters."""
    path = "/sys/class/net/eth0/statistics"
    out = sh(f"ip netns exec {ns(i)} sh -c 'cat {path}/tx_bytes {path}/rx_bytes {path}/tx_packets {path}/rx_packets; cat /proc/net/snmp'")
    lines = out.split("\n")
    values = {"tx_bytes": int(lines[0]), "rx_bytes": int(lines[1]), "tx_packets": int(lines[2]), "rx_packets": int(lines[3])}
    snmp = lines[4:]
    for k in range(0, len(snmp) - 1, 2):
        header, data = snmp[k].split(), snmp[k + 1].split()
        if not header:
            continue
        if header[0] == "Udp:":
            row = dict(zip(header[1:], data[1:]))
            values["udp_rcvbuf_errors"] = int(row.get("RcvbufErrors", 0))
            values["udp_in"] = int(row.get("InDatagrams", 0))
        elif header[0] == "Tcp:":
            row = dict(zip(header[1:], data[1:]))
            values["tcp_retrans"] = int(row.get("RetransSegs", 0))
            values["tcp_out"] = int(row.get("OutSegs", 0))
    return values


def host_cpu():
    """Host-wide CPU time from /proc/stat in milliseconds: (busy, idle, steal), for contention from other workloads."""
    if not os.path.exists("/proc/stat"):
        return None
    with open("/proc/stat") as f:
        fields = [int(x) for x in f.readline().split()[1:]]
    tick_ms = 1000.0 / os.sysconf("SC_CLK_TCK")
    user, nice, system, idle, iowait, irq, softirq, steal = (fields + [0] * 8)[:8]
    return ((user + nice + system + irq + softirq) * tick_ms, (idle + iowait) * tick_ms, steal * tick_ms)


def percentile(values, q):
    if not values:
        return None
    ordered = sorted(values)
    return ordered[min(len(ordered) - 1, max(0, math.ceil(q * len(ordered)) - 1))]


def origination_metrics(records, interval_ms, transactions_per_object, kind):
    """Actual released objects, including scheduler lateness; records are (actual_ns, scheduled_ns)."""
    actual = [actual for actual, _ in records]
    late = [(actual - scheduled) / 1e6 for actual, scheduled in records]
    first, last = (min(actual), max(actual)) if actual else (None, None)
    span = (last - first) / 1e9 if actual else None
    # Add one release interval so a burst at t=0 followed by one at t=1 covers two intervals.
    window = span + interval_ms / 1000 if span is not None else None
    rate = len(actual) / window if window else None
    return {
        "released_objects": len(actual), "actual_first_origin_ns": first, "actual_last_origin_ns": last,
        "actual_origin_span_s": span, "actual_offered_window_s": window,
        "actual_offered_objects_per_s": rate,
        "actual_offered_tx_per_s": rate * transactions_per_object if rate is not None and kind == 1 else None,
        "origin_schedule_lateness_ms": {"p50": percentile(late, .5), "p95": percentile(late, .95),
                                        "max": max(late) if late else None, "min": min(late) if late else None},
    }


def receiver_metrics(results, nodes, edges, indices, origin):
    """Count observed receiver delivery even when a failed origin left no timestamp."""
    far, by_hops = [], {}
    deliveries = completed = unknown_objects = unknown_deliveries = 0
    indices = list(indices)
    for index in indices:
        source = origin(index)
        started = results.get(source, {}).get("originated", {}).get(str(index))
        received = [(node, result.get("completed", {}).get(str(index)))
                    for node, result in results.items() if node != source]
        received = [(node, when) for node, when in received if when is not None]
        deliveries += len(received)
        complete = len(received) == nodes - 1
        completed += complete
        if started is None:
            unknown_objects += bool(received)
            unknown_deliveries += len(received)
            continue
        dist = distances(nodes, edges, source)
        latency = [(node, (when - started) / 1e6) for node, when in received]
        for node, value in latency:
            by_hops.setdefault(dist[node], []).append(value)
        if complete and latency:
            far.append(max(value for _, value in latency))
    return {
        "validated_receiver_deliveries": deliveries,
        "expected_receiver_deliveries": len(indices) * (nodes - 1),
        "completed": completed, "missing": len(indices) - completed,
        "latency_sampled_objects": len(far),
        "latency_unknown_objects": unknown_objects,
        "latency_unknown_receiver_deliveries": unknown_deliveries,
        "latency_ms": {"p50": percentile(far, .5), "p95": percentile(far, .95), "p99": percentile(far, .99),
                       "max": max(far) if far else None, "mean": statistics.mean(far) if far else None},
        "latency_by_hops_p50_ms": {str(h): percentile(values, .5) for h, values in sorted(by_hops.items())},
    }


def goodput_window(results, offered_seconds, controller_seconds):
    if not results:
        return None
    observed = []
    for result in results.values():
        timestamps = [result["finished_ns"]]
        for field in ("originated", "completed"):
            timestamps.extend(result.get(field, {}).values())
        observed.append((max(timestamps) - result["measured_ns"]) / 1e9)
    return max(offered_seconds, controller_seconds, *observed, 1e-9)


def broadcast_start(earliest_ns, genesis_ns):
    """Use a fresh duty slot in the shared 12-second benchmark profile."""
    slot_ns = 12_000_000_000
    slot = (max(0, earliest_ns - genesis_ns) + slot_ns - 1) // slot_ns
    return genesis_ns + slot * slot_ns + 100_000_000


def prepare_measurement(run_dir, nodes, enabled, sample, clock=time.time_ns, broadcast_genesis_ns=None):
    """Finish baseline reads before publishing the timestamp that releases measured work."""
    before, window = None, None
    if enabled:
        started = clock()
        before = {node: sample(node) for node in range(nodes)}
        window = {"before_started_unix_ns": started, "before_finished_unix_ns": clock(),
                  "scope": "post-warmup baseline before measurement scheduling through controller stop"}
    measured = clock() + 4_000_000_000
    if broadcast_genesis_ns is not None:
        measured = broadcast_start(measured, broadcast_genesis_ns)
    temporary = os.path.join(run_dir, "measure.tmp")
    with open(temporary, "w") as stream:
        stream.write(str(measured))
    os.rename(temporary, os.path.join(run_dir, "measure"))
    return measured, before, window


def capture_wire_end(window, nodes, sample, complete, clock=time.time_ns):
    if window is None:
        return None
    window["comparison_eligible"] = complete
    window["partial_scope"] = not complete
    window["after_started_unix_ns"] = clock()
    values = {node: sample(node) for node in range(nodes)}
    window["after_finished_unix_ns"] = clock()
    return values


def finish_processes(processes, timeout, complete):
    """Let surviving nodes reach their own deadline and retain partial results after a peer exits."""
    grace = 60 if complete else timeout + 30
    for process, log in processes:
        try:
            process.wait(timeout=grace)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=10)
        log.close()
    return grace


def cpu_snapshot_quality(results, warmup, expected_nodes):
    """Conservative eligibility for CPU comparisons; raw measured deltas remain available."""
    issues = []
    tolerance_ns = 10_000_000
    if len(results) != expected_nodes:
        issues.append("missing node results")
    for node, result in sorted(results.items()):
        required = ("idle_snapshot_ns", "snapshot_before_ns", "snapshot_after_ns", "measured_ns",
                    "warmup_completed_at_idle", "warmup_completed_at_start")
        if any(key not in result for key in required):
            issues.append(f"node {node}: missing snapshot metadata")
            continue
        idle, before, after = (result[key] for key in required[:3])
        measured = result["measured_ns"]
        if not measured - 2_000_000_000 <= idle <= measured - 2_000_000_000 + tolerance_ns:
            issues.append(f"node {node}: late idle snapshot")
        if not measured <= before <= measured + tolerance_ns or after <= before or before - idle < 1_900_000_000:
            issues.append(f"node {node}: late or invalid measurement snapshot")
        if result["warmup_completed_at_idle"] != warmup or result["warmup_completed_at_start"] != warmup:
            issues.append(f"node {node}: unfinished warmup at CPU baseline")
        activity = [when for field in ("originated", "completed") for index, when in result.get(field, {}).items()
                    if int(index) >= warmup]
        if activity and min(activity) < before:
            issues.append(f"node {node}: measured activity precedes CPU snapshot")
    return {"eligible": not issues, "issues": issues, "late_tolerance_ms": tolerance_ns / 1e6,
            "policy": "all warmup complete before idle/start; timely snapshots; no measured activity before start snapshot"}


def wait_warmup(run_dir, nodes, processes, timeout):
    """Wait for every node's validated/recovered warmup marker before scheduling measurement."""
    started = time.monotonic()
    while True:
        completed = {}
        for node in range(nodes):
            path = os.path.join(run_dir, f"warmup.{node}")
            if os.path.exists(path):
                with open(path) as stream:
                    completed[str(node)] = int(stream.read().strip())
        now = time.monotonic()
        status = {"completed_nodes": len(completed), "expected_nodes": nodes, "completed_unix_ns": completed,
                  "observed_unix_ns": time.time_ns(), "wait_s": now - started}
        if len(completed) == nodes:
            return status
        if now - started >= timeout or any(process.poll() is not None for process, _ in processes):
            status["error"] = "not all nodes completed warmup before the deadline or a node exited"
            return status
        time.sleep(0.05)


def retain_process_failures(summary, exit_codes):
    """Retain controller aborts and child failures even when partial measurements were written."""
    completion = summary.get("completion_wait", {})
    if completion.get("all_nodes_done") is False:
        message = "completion wait aborted: " + completion.get("abort_reason", "not all nodes done")
        summary["error"] = f"{summary['error']}; {message}" if summary.get("error") else message
    summary["node_exit_codes"] = {str(node): code for node, code in exit_codes.items()}
    failed = [f"node {node}: {code if code is not None else 'did not exit'}" for node, code in exit_codes.items() if code != 0]
    if failed:
        message = "node process failure (" + ", ".join(failed) + ")"
        summary["error"] = f"{summary['error']}; {message}" if summary.get("error") else message


def run(args):
    n, edges = topology(args.topology)
    run_dir = os.path.abspath(os.path.join(args.out, args.label or time.strftime("run-%Y%m%d-%H%M%S")))
    os.makedirs(run_dir, exist_ok=False)
    digest = hashlib.sha256()
    with open(args.test_dll, "rb") as binary:
        for chunk in iter(lambda: binary.read(1024 * 1024), b""):
            digest.update(chunk)
    binary_hash = digest.hexdigest()
    repo = os.path.abspath(os.path.join(os.path.dirname(__file__), "../.."))
    git_revision = subprocess.run(["git", "-C", repo, "rev-parse", "HEAD"], capture_output=True, text=True)
    revision = git_revision.stdout.strip() if git_revision.returncode == 0 else os.environ.get("LEAN_NET_SOURCE_REVISION", "unknown")
    dirty = subprocess.run(["git", "-C", repo, "status", "--porcelain"], capture_output=True, text=True).stdout.strip() if git_revision.returncode == 0 else None
    provenance = {"source_revision": revision, "source_dirty": bool(dirty) if dirty is not None else None,
                  "binary_build_source_revision": os.environ.get("LEAN_NET_BINARY_SOURCE_REVISION", revision),
                  "source_archive_sha256": os.environ.get("LEAN_NET_SOURCE_ARCHIVE_SHA256"), "test_dll_sha256": binary_hash,
                  "os": platform.platform(), "python": platform.python_version(),
                  "dotnet": subprocess.check_output([args.dotnet, "--version"], text=True).strip(),
                  "installed_runtimes": subprocess.check_output([args.dotnet, "--list-runtimes"], text=True).strip(),
                  "arguments": vars(args), "verification": "controlled test verifier; transport results are not native proving throughput"}
    with open(os.path.join(run_dir, "provenance.json"), "w") as f:
        json.dump(provenance, f, indent=2)
    processes = []
    summary = {"label": args.label, "topology": args.topology, "nodes": n, "binding": args.binding, "broadcast": args.broadcast,
               "kind": args.kind, "objects": args.objects, "warmup": args.warmup, "proof_bytes": args.proof_bytes, "transactions_per_object": args.transactions_per_object, "burst": args.burst,
               "interval_ms": args.interval_ms, "origins": args.origins, "verify_ms": args.verify_ms, "rtt_ms": args.rtt_ms,
               "loss": args.loss, "rate_mbit": args.rate_mbit, "sysctl": args.sysctl, "load": open("/proc/loadavg").read().split()[:3] if os.path.exists("/proc/loadavg") else None}
    summary["provenance"] = provenance
    summary["run_dir"] = run_dir
    try:
        if args.netns:
            setup(n)
            shape(n, edges, args.rtt_ms, args.loss, args.rate_mbit)
            for setting in args.sysctl:
                for i in range(n):
                    sh(f"ip netns exec {ns(i)} sysctl -qw {setting}")

        neighbours = {i: [] for i in range(n)}
        for a, b in edges:
            neighbours[a].append(b)
            neighbours[b].append(a)
        port = QUIC_PORT if args.binding == "ethp2p" else RLPX_PORT
        genesis_ms = int(time.time() * 1000)

        def address(i):
            return (ip(i), port) if args.netns else ("127.0.0.1", port + 10 * i + 100)

        for i in range(n):
            config = {
                "id": i, "binding": args.binding, "broadcast": args.broadcast, "ip": address(i)[0], "port": address(i)[1],
                "peers": [{"id": p, "ip": address(p)[0], "port": address(p)[1]} for p in neighbours[i]],
                "nodes": n, "kind": args.kind, "objects": args.warmup + args.objects, "warmup": args.warmup, "proofBytes": args.proof_bytes, "transactionsPerObject": args.transactions_per_object, "burst": args.burst,
                "intervalMs": args.interval_ms, "origins": args.origins, "verifyMs": args.verify_ms, "genesisUnixMs": genesis_ms,
                "runDir": run_dir, "timeoutSec": args.timeout
            }
            path = os.path.join(run_dir, f"config.{i}.json")
            with open(path, "w") as f:
                json.dump(config, f)
            # Runs last seconds, so tiered recompilation would dominate their CPU; full JIT happens during warm-up instead.
            env = dict(os.environ, LEAN_NET_CONFIG=path, DOTNET_gcServer="0", DOTNET_TieredCompilation="1" if args.tiered else "0")
            command = [args.dotnet, "exec", args.test_dll, "--filter", "FullyQualifiedName~LeanNetBenchNode", "--no-progress",
                       "--results-directory", os.path.join(run_dir, f"tr.{i}")]
            if args.netns:
                command = ["ip", "netns", "exec", ns(i)] + command
            if args.cpus:
                command = ["taskset", "-c", args.cpus] + command
            log = open(os.path.join(run_dir, f"node.{i}.log"), "w")
            processes.append((subprocess.Popen(command, env=env, stdout=log, stderr=subprocess.STDOUT), log))

        def wait_files(prefix, timeout):
            deadline = time.time() + timeout
            while time.time() < deadline:
                if all(os.path.exists(os.path.join(run_dir, f"{prefix}.{i}")) for i in range(n)):
                    return True
                if any(p.poll() is not None for p, _ in processes):
                    return False
                time.sleep(0.05)
            return False

        cpu_before = cpu_after = None
        before = after = None
        if not wait_files("ready", 120):
            summary["error"] = "nodes did not connect"
            return summary
        start_ns = time.time_ns() + 500_000_000
        with open(os.path.join(run_dir, "start.tmp"), "w") as f:
            f.write(str(start_ns))
        os.rename(os.path.join(run_dir, "start.tmp"), os.path.join(run_dir, "start"))
        summary["warmup_barrier"] = wait_warmup(run_dir, n, processes, args.timeout)
        if summary["warmup_barrier"].get("error"):
            summary["error"] = summary["warmup_barrier"]["error"]
            return summary
        # Every warmup has completed. Allow two seconds of idle calibration plus two seconds to publish and schedule it.
        measured_ns, before, wire_window = prepare_measurement(run_dir, n, args.netns, counters,
            broadcast_genesis_ns=genesis_ms * 1_000_000 if args.broadcast else None)
        if wire_window is not None:
            summary["wire_counter_window"] = wire_window
        summary["warmup_barrier"]["measured_start_unix_ns"] = measured_ns
        summary["warmup_barrier"]["minimum_settle_s"] = 4
        summary["warmup_barrier"]["scheduled_settle_s"] = (measured_ns - summary["warmup_barrier"]["observed_unix_ns"]) / 1e9
        if args.broadcast:
            summary["warmup_barrier"]["broadcast_slot_start_unix_ns"] = measured_ns - 100_000_000
            summary["warmup_barrier"]["broadcast_slot_guard_ms"] = 100
        time.sleep(max(0.0, (measured_ns - 20_000_000 - time.time_ns()) / 1e9))
        cpu_before, wall_before = host_cpu(), time.time()
        all_done = wait_files("done", args.timeout + 30)
        summary["completion_wait"] = {"all_nodes_done": all_done,
                                      "abort_reason": None if all_done else "node exited" if any(p.poll() is not None for p, _ in processes) else "deadline"}
        after = capture_wire_end(wire_window, n, counters, all_done)
        summary["measurement_stopped_unix_ns"] = time.time_ns()
        cpu_after, wall_after = host_cpu(), time.time()
        open(os.path.join(run_dir, "stop"), "w").close()
        summary["completion_wait"]["exit_grace_s"] = finish_processes(processes, args.timeout, all_done)
    finally:
        for p, log in processes:
            if p.poll() is None:
                p.kill()
                p.wait(timeout=10)
            if not log.closed:
                log.close()
        retain_process_failures(summary, {i: process.returncode for i, (process, _) in enumerate(processes)})
        if args.netns and not args.keep:
            teardown()

    results = {}
    for i in range(n):
        path = os.path.join(run_dir, f"result.{i}.json")
        if os.path.exists(path):
            with open(path) as f:
                results[i] = json.load(f)
    if len(results) < n:
        message = f"only {len(results)} of {n} results"
        summary["error"] = f"{summary['error']}; {message}" if summary.get("error") else message
        if not results:
            return summary

    def origin(index):
        return index % n if args.origins == "all" else int(args.origins)

    body_bytes = next((r.get("body_bytes") for r in results.values() if r.get("body_bytes") is not None), None)
    measured_indices = range(args.warmup, args.warmup + args.objects)
    summary.update(receiver_metrics(results, n, edges, measured_indices, origin))
    deliveries = summary["validated_receiver_deliveries"]
    useful_bytes = deliveries * body_bytes if body_bytes is not None else None
    origin_records = []
    for index in measured_indices:
        source = results.get(origin(index), {})
        actual = source.get("originated", {}).get(str(index))
        if actual is not None:
            scheduled = source["measured_ns"] + ((index - args.warmup) // args.burst) * args.interval_ms * 1_000_000
            origin_records.append((actual, scheduled))
    # Include the whole offered-load window and any drain time. Never divide only by successful-transfer latency.
    offered_window_s = math.ceil(args.objects / args.burst) * args.interval_ms / 1000
    controller_window_s = max(0.0, (summary["measurement_stopped_unix_ns"] - measured_ns) / 1e9)
    window_s = goodput_window(results, offered_window_s, controller_window_s)
    summary["controller_observed_window_s"] = controller_window_s
    summary.update(origination_metrics(origin_records, args.interval_ms, args.transactions_per_object, args.kind))
    summary["body_bytes"] = body_bytes
    summary["goodput_window_s"] = window_s
    summary["offered_objects_per_s"] = args.burst * 1000 / args.interval_ms
    summary["goodput_objects_per_s_per_receiver"] = deliveries / (n - 1) / window_s
    summary["goodput_tx_per_s_per_receiver"] = summary["goodput_objects_per_s_per_receiver"] * args.transactions_per_object if args.kind == 1 else None
    summary["goodput_mbit_per_s_per_receiver"] = useful_bytes * 8 / (n - 1) / window_s / 1e6 if body_bytes is not None else None
    cpu = sum(r["cpu_user_ms"] + r["cpu_sys_ms"] for r in results.values())
    summary["cpu_ms_total"] = cpu
    summary["cpu_ms_per_object"] = cpu / args.objects
    idle = sum(r["idle_cpu_ms_per_s"] * r["window_ms"] / 1000 for r in results.values())
    summary["idle_cpu_ms_per_s"] = sum(r["idle_cpu_ms_per_s"] for r in results.values())
    summary["cpu_snapshot_quality"] = cpu_snapshot_quality(results, args.warmup, n)
    summary["cpu_comparison_eligible"] = summary["cpu_snapshot_quality"]["eligible"]
    summary["cpu_net_ms_per_object"] = max(0.0, cpu - idle) / args.objects if summary["cpu_comparison_eligible"] else None
    summary["cpu_net_scope"] = "idle-adjusted estimate; raw cpu_ms_total is authoritative for actual snapshot windows"
    origins = {origin(index) for index in measured_indices}
    summary["cpu_ms_origin_per_object"] = sum(results[o]["cpu_user_ms"] + results[o]["cpu_sys_ms"] for o in origins if o in results) / args.objects
    summary["alloc_mb_per_object"] = sum(r["allocated_bytes"] for r in results.values()) / args.objects / 2**20
    summary["peak_rss_mb_max"] = max(r["peak_rss_kb"] for r in results.values()) / 1024
    summary["rss_growth_mb_max"] = max(r["rss_after_kb"] - r["rss_before_kb"] for r in results.values()) / 1024
    if cpu_before and cpu_after:
        busy, idle_host, steal = (a - b for a, b in zip(cpu_after, cpu_before))
        total = busy + idle_host + steal
        # The nodes' own CPU covers a slightly longer window (until stop), so "other" is a lower bound on contention.
        summary["host"] = {"cores": os.cpu_count(), "window_s": wall_after - wall_before, "busy_pct": 100 * busy / total if total else None,
                           "steal_pct": 100 * steal / total if total else None,
                           "other_cores": max(0.0, busy - cpu) / 1000 / (wall_after - wall_before) if wall_after > wall_before else None}
    summary["app_bytes"] = sum(r["rlpx_bytes_sent"] for r in results.values()) + sum(
        v for r in results.values() for k, v in r["ethp2p_stream_bytes"].items() if k.startswith("out/"))
    summary["redials"] = sum(r.get("redials", 0) for r in results.values())
    summary["peers_lost_at_end"] = sum(max(0, len(neighbours[i]) - r.get("peers_at_end", 0)) for i, r in results.items())
    summary["broadcast_shards_accepted_total_including_warmup"] = sum(r.get("shards_accepted", 0) for r in results.values())
    summary["broadcast_shards_accepted_measured"] = sum(r["shards_accepted_measured"] for r in results.values()) \
        if len(results) == n and all("shards_accepted_measured" in r for r in results.values()) else None
    summary["coding_ms_total"] = sum(r["coding_micros"] for r in results.values()) / 1000
    for field in ("transfers", "events"):
        merged = {}
        for r in results.values():
            for k, v in r.get(field, {}).items():
                if v:
                    merged[k] = merged.get(k, 0) + v
        summary[field] = merged
    if after and before:
        tx = sum(after[i]["tx_bytes"] - before[i]["tx_bytes"] for i in range(n))
        summary["wire_bytes"] = tx
        summary["wire_packets"] = sum(after[i]["tx_packets"] - before[i]["tx_packets"] for i in range(n))
        summary["udp_rcvbuf_errors"] = sum(after[i].get("udp_rcvbuf_errors", 0) - before[i].get("udp_rcvbuf_errors", 0) for i in range(n))
        summary["tcp_retrans"] = sum(after[i].get("tcp_retrans", 0) - before[i].get("tcp_retrans", 0) for i in range(n))
        summary["origin_tx_bytes"] = sum(after[o]["tx_bytes"] - before[o]["tx_bytes"] for o in origins)
    summary["run_dir"] = run_dir
    return summary


def main():
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest="command", required=True)
    r = sub.add_parser("run")
    r.add_argument("--topology", default="pair")
    r.add_argument("--binding", choices=["rlpx", "ethp2p"], default="rlpx")
    r.add_argument("--broadcast", action="store_true")
    r.add_argument("--kind", type=int, choices=[1, 2], default=1)
    r.add_argument("--objects", type=int, default=20)
    r.add_argument("--warmup", type=int, default=3)
    r.add_argument("--proof-bytes", type=int, default=270_000)
    r.add_argument("--transactions-per-object", type=int, choices=[1, 16, 64], default=1)
    r.add_argument("--burst", type=int, default=1)
    r.add_argument("--interval-ms", type=int, default=1000)
    r.add_argument("--origins", default="0")
    r.add_argument("--verify-ms", type=int, default=0)
    r.add_argument("--rtt-ms", type=float, default=0)
    r.add_argument("--loss", type=float, default=0)
    r.add_argument("--rate-mbit", type=int, default=100)
    r.add_argument("--timeout", type=int, default=120)
    r.add_argument("--netns", action="store_true")
    r.add_argument("--tiered", action="store_true", help="keep tiered compilation (default: fully optimizing JIT only)")
    r.add_argument("--keep", action="store_true")
    r.add_argument("--sysctl", action="append", default=[], help="per-namespace sysctl, e.g. net.ipv4.tcp_slow_start_after_idle=0")
    r.add_argument("--cpus", default=None)
    r.add_argument("--label", default=None)
    r.add_argument("--out", default="runs")
    r.add_argument("--results", default=None, help="append the summary to this JSONL file")
    r.add_argument("--dotnet", default="dotnet")
    r.add_argument("--test-dll", required=True)
    sub.add_parser("teardown")
    args = parser.parse_args()
    if not re.fullmatch(r"[a-zA-Z0-9_-]{1,12}", PREFIX):
        parser.error("LEAN_NET_PREFIX must be 1–12 letters, digits, underscores or hyphens")
    if not re.fullmatch(r"10\.(?:[0-9]{1,3})", NET) or int(NET.split(".")[1]) > 255:
        parser.error("LEAN_NET_SUBNET must be a private 10.x prefix")
    if args.command == "teardown":
        teardown()
        return
    if args.objects <= 0 or args.burst <= 0 or args.interval_ms <= 0 or args.warmup < 0:
        parser.error("objects, burst and interval must be positive; warmup must be nonnegative")
    if args.proof_bytes < 0 or args.proof_bytes > 8 * 1024 * 1024:
        parser.error("proof bytes must be between zero and 8 MiB")
    if args.rtt_ms < 0 or not 0 <= args.loss <= 100 or args.rate_mbit < 0:
        parser.error("RTT and rate must be nonnegative; loss must be between 0 and 100")
    if args.broadcast and (args.interval_ms < 12000 or args.burst != 1):
        parser.error("broadcast requires one object per 12-second slot")
    if args.broadcast and args.kind != 2:
        parser.error("broadcast is defined only for kind 2")
    try:
        summary = run(args)
    except Exception as error:
        traceback.print_exc()
        summary = {"label": args.label, "error": str(error), "binding": args.binding,
                   "kind": args.kind, "transactions_per_object": args.transactions_per_object,
                   "objects": args.objects, "burst": args.burst, "interval_ms": args.interval_ms,
                   "offered_objects_per_s": args.burst * 1000 / args.interval_ms,
                   "proof_bytes": args.proof_bytes, "topology": args.topology, "rtt_ms": args.rtt_ms,
                   "rate_mbit": args.rate_mbit, "loss": args.loss}
        if args.label:
            provenance = os.path.join(args.out, args.label, "provenance.json")
            if os.path.exists(provenance):
                with open(provenance) as stream:
                    summary["provenance"] = json.load(stream)
    line = json.dumps(summary)
    print(line)
    if args.results:
        with open(args.results, "a") as f:
            f.write(line + "\n")


if __name__ == "__main__":
    main()
