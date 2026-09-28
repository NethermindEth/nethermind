"""Scenario orchestration: warm up on baseline traffic, add the attacker, measure, report."""
from __future__ import annotations

import os
import threading
import time
from concurrent.futures import ThreadPoolExecutor

from .results import LatencySeries, percentile
from .roles import Context, RpcError

MAX_INFLIGHT = 64

# Only ever reaches a fee field of a transaction that is built and thrown away.
CALIBRATION_BASE_FEE = 1_000_000_000


class HeadTracker:
    """Samples each node's head so a run records block production without needing Prometheus.

    Prometheus remains the source of truth for client-side timing; this exists so a smoke run
    can prove blocks are being produced from the generator's own output."""

    def __init__(self, nodes, recorder, interval: float = 1.0):
        self._nodes = nodes
        self._recorder = recorder
        self._interval = interval
        self._stop = threading.Event()
        self._thread = threading.Thread(target=self._loop, name="head-tracker", daemon=True)
        self._seen: dict[str, dict[int, float]] = {node.name: {} for node in nodes}

    def start(self) -> None:
        self._thread.start()

    def stop(self) -> None:
        self._stop.set()
        self._thread.join(timeout=5.0)

    def _loop(self) -> None:
        while not self._stop.is_set():
            for node in self._nodes:
                try:
                    block = node.get_block("latest")
                except (RpcError, OSError):
                    continue
                if not block:
                    continue
                number = int(block["number"], 16)
                if number not in self._seen[node.name]:
                    self._seen[node.name][number] = int(block["timestamp"], 16)
            self._stop.wait(self._interval)

    def report(self) -> None:
        for node in self._nodes:
            blocks = self._seen[node.name]
            if not blocks:
                self._recorder.emit("head_progress", node=node.name, client=node.client, blocks=0)
                continue
            numbers = sorted(blocks)
            gaps = [
                blocks[numbers[i + 1]] - blocks[numbers[i]]
                for i in range(len(numbers) - 1)
                if blocks[numbers[i + 1]] >= blocks[numbers[i]]
            ]
            self._recorder.emit(
                "head_progress",
                node=node.name,
                client=node.client,
                blocks=len(numbers),
                first_block=numbers[0],
                last_block=numbers[-1],
                mean_block_interval_s=(sum(gaps) / len(gaps)) if gaps else 0.0,
            )


class ChainWatcher:
    """Resolves inclusion by walking blocks instead of polling a receipt per transaction.

    The per-transaction poll this replaces had two faults that only appeared under load. It was
    the largest single source of RPC traffic the measurement itself put on the node, and its
    per-transaction deadline recorded a slow inclusion as no inclusion at all, so every run's
    longest observed latency sat at the deadline rather than at anything the chain did. Walking
    the chain costs one call per block however many transactions are outstanding, resolves each
    one whenever it actually lands, and yields the per-block transaction count for free.

    Latency is measured from submission to the including block's timestamp, so it carries the
    chain's own one-second granularity rather than this thread's polling jitter."""

    def __init__(self, node, recorder, poll: float = 1.0):
        self._node = node
        self._recorder = recorder
        self._poll = poll
        self._lock = threading.Lock()
        self._pending: dict[str, tuple[object, float]] = {}
        self._resolved: dict[int, list[float]] = {}
        self._blocks: list[tuple[int, int, int]] = []
        self._next: int | None = None
        self._stop = threading.Event()
        self._thread = threading.Thread(target=self._loop, name="chain-watcher", daemon=True)

    def start(self) -> None:
        self._thread.start()

    def register(self, owner, tx_hash: str, submitted_at: float) -> None:
        with self._lock:
            self._pending[tx_hash.lower()] = (owner, submitted_at)

    def settle(self, seconds: float) -> None:
        """Keeps walking after the load stops, so a transaction admitted at the end of the
        measured window gets the same chance to be included as one admitted in the middle."""
        self._stop.wait(seconds)

    def stop(self) -> None:
        self._stop.set()
        self._thread.join(timeout=self._poll * 5 + 10.0)

    def stats(self, owner) -> tuple[list[float], int]:
        """Inclusion latencies in ms, and how many of this owner's transactions are still
        outstanding. The second figure is the censoring the old deadline used to hide."""
        with self._lock:
            latencies = sorted(self._resolved.get(id(owner), []))
            censored = sum(1 for held, _ in self._pending.values() if held is owner)
        return latencies, censored

    def report_blocks(self) -> None:
        with self._lock:
            blocks = list(self._blocks)
        counts = [count for _, _, count in blocks]
        if not counts:
            self._recorder.emit("block_fill", node=self._node.name, blocks=0)
            return
        self._recorder.emit(
            "block_fill",
            node=self._node.name,
            client=self._node.client,
            blocks=len(counts),
            first_block=blocks[0][0],
            last_block=blocks[-1][0],
            txs_total=sum(counts),
            txs_per_block_mean=round(sum(counts) / len(counts), 2),
            txs_per_block_max=max(counts),
            empty_blocks=sum(1 for count in counts if count == 0),
        )

    def _loop(self) -> None:
        while not self._stop.is_set():
            try:
                head = self._node.block_number()
            except (RpcError, OSError):
                self._stop.wait(self._poll)
                continue
            if self._next is None:
                self._next = head
            while self._next <= head and self._ingest(self._next):
                self._next += 1
            self._stop.wait(self._poll)

    def _ingest(self, number: int) -> bool:
        try:
            block = self._node.get_block(number, full=False)
        except (RpcError, OSError):
            return False
        if not block:
            return False
        timestamp = int(block["timestamp"], 16)
        hashes = block.get("transactions") or []
        landed = []
        with self._lock:
            self._blocks.append((number, timestamp, len(hashes)))
            for entry in hashes:
                key = (entry if isinstance(entry, str) else entry["hash"]).lower()
                held = self._pending.pop(key, None)
                if held is None:
                    continue
                owner, submitted_at = held
                latency_ms = max(timestamp - submitted_at, 0.0) * 1000.0
                self._resolved.setdefault(id(owner), []).append(latency_ms)
                landed.append((key, latency_ms, owner))
        for key, latency_ms, owner in landed:
            self._recorder.event(kind="inclusion", tx_hash=key, node=self._node.name,
                                 included=True, block=number,
                                 inclusion_latency_ms=round(latency_ms, 1))
            if hasattr(owner, "on_included"):
                owner.on_included(key)
        return True


class Submitter:
    """Paces one role at an offered rate, round-robin across every node.

    Round-robin rather than one node: an invalid frame transaction is rejected at admission
    and never gossiped, so a node only pays for the shapes submitted to it directly. Both
    clients have to be submitted to for a cross-client claim to mean anything."""

    def __init__(self, role, nonces, ctx, rate: float, recorder, track_inclusion: bool = False,
                 nodes=None, watcher=None):
        self.role = role
        # Hands out (sender, nonce): accounts.FixedNonce for attackers, SenderRotation for
        # honest traffic that has to land.
        self.nonces = nonces
        self.ctx = ctx
        self.rate = rate
        self.recorder = recorder
        self.track_inclusion = track_inclusion
        # Inclusion is resolved by ChainWatcher, off this class's thread pool: a per-transaction
        # waiter used to share the pool with submission, so a slow chain throttled the offered load.
        self.watcher = watcher
        # Which nodes this role submits to. Defaults to every node; a scenario that wants to
        # separate the attacked node from the observed one passes a subset.
        self.nodes = list(nodes) if nodes else list(ctx.nodes)
        self.series: dict[str, LatencySeries] = {node.name: LatencySeries() for node in self.nodes}
        self.submitted = 0
        self._lock = threading.Lock()
        self._stop = threading.Event()
        self._thread = None
        self._pool = ThreadPoolExecutor(max_workers=MAX_INFLIGHT, thread_name_prefix=role.name)
        self._started_at = 0.0
        self._finished_at = 0.0
        self._salt = 0
        # What the pacing loop asked for, against self.submitted, which is what was built and
        # sent. A gap means the generator could not keep up and the offered rate is fiction.
        self._scheduled = 0
        # Slots skipped because every honest sender still had a transaction in flight.
        self.starved = 0
        self._base_fee = 0
        self._base_fee_at = 0.0

    def start(self) -> None:
        if self.rate <= 0:
            return
        self.nonces.sync(self.ctx.submit_node)
        self._started_at = time.monotonic()
        self._thread = threading.Thread(target=self._loop, name="submit-" + self.role.name, daemon=True)
        self._thread.start()

    def stop(self) -> None:
        self._stop.set()
        if self._thread is not None:
            self._thread.join(timeout=10.0)
        self._finished_at = time.monotonic()
        # Cancel rather than drain. The pool's queue is unbounded, so a generator that cannot
        # keep up accumulates a backlog; draining it would build and submit transactions after
        # the measured window closed and count them against the window's length, reporting an
        # offered rate that was never offered.
        self._pool.shutdown(wait=True, cancel_futures=True)

    def _next_salt(self) -> bytes:
        with self._lock:
            self._salt += 1
            return self._salt.to_bytes(8, "big")

    def _cached_base_fee(self) -> int:
        now = time.monotonic()
        if now - self._base_fee_at > 2.0:
            try:
                self._base_fee = self.ctx.submit_node.base_fee()
            except (RpcError, OSError):
                pass
            self._base_fee_at = now
        return self._base_fee

    def _loop(self) -> None:
        interval = 1.0 / self.rate
        node_count = len(self.nodes)
        index = 0
        next_at = time.monotonic()
        deadline = self.ctx.deadline()
        deadline_refreshed = time.monotonic()

        while not self._stop.is_set():
            now = time.monotonic()
            if now < next_at:
                self._stop.wait(min(next_at - now, 0.05))
                continue
            next_at += interval
            # A stale deadline would evict every transaction on arrival, so refresh it once a
            # slot rather than per transaction (which would cost an RPC round trip each time).
            if now - deadline_refreshed > self.ctx.seconds_per_slot:
                deadline = self.ctx.deadline()
                deadline_refreshed = now

            picked = self.nonces.acquire()
            if picked is None:
                with self._lock:
                    self.starved += 1
                continue
            sender, nonce = picked
            node = self.nodes[index % node_count]
            index += 1
            salt = self._next_salt()
            tx_deadline = deadline if getattr(self.role, "uses_deadline", True) else None
            base_fee = self._cached_base_fee()
            with self._lock:
                self._scheduled += 1
            self._pool.submit(self._submit_one, node, sender, nonce, salt, base_fee, tx_deadline)

    def on_included(self, tx_hash: str) -> None:
        self.nonces.included(tx_hash)

    def _submit_one(self, node, sender, nonce: int, salt: bytes, base_fee: int, deadline) -> None:
        try:
            built = self.role.build(self.ctx, sender, nonce, base_fee, salt, deadline)
        except Exception as error:  # a build failure is a bug in the shape, not a measurement
            self.nonces.submitted(sender, "", False)
            self.recorder.event(kind="build_error", role=self.role.name, error=str(error))
            return

        with self._lock:
            self.submitted += 1

        started = time.perf_counter()
        started_wall = time.time()
        tx_hash, outcome, reason = "", "errored", ""
        try:
            tx_hash = node.send_raw(built.raw)
            outcome = "accepted"
        except RpcError as error:
            outcome, reason = "rejected", error.message
        except OSError as error:
            outcome, reason = "errored", "transport: {0}".format(error)
        micros = (time.perf_counter() - started) * 1e6
        self.nonces.submitted(sender, tx_hash, outcome == "accepted")

        self.series[node.name].add(micros, outcome, reason)
        self.recorder.event(
            kind="submission",
            role=self.role.name,
            shape=built.shape,
            node=node.name,
            client=node.client,
            sender=sender.address,
            nonce=nonce,
            declared_verify_gas=built.declared_verify_gas,
            submit_us=round(micros, 1),
            outcome=outcome,
            reason=reason,
            tx_hash=tx_hash,
        )

        if outcome == "accepted" and self.track_inclusion and self.watcher is not None:
            self.watcher.register(self, tx_hash, started_wall)

    def report(self, phase: str) -> None:
        elapsed = max((self._finished_at or time.monotonic()) - self._started_at, 1e-9)
        for node in self.nodes:
            summary = self.series[node.name].summary()
            self.recorder.emit(
                "admission",
                phase=phase,
                role=self.role.name,
                shape=self.role.shape,
                node=node.name,
                client=node.client,
                ceiling=self.ctx.ceiling,
                k_retry=self.ctx.k_retry,
                offered_rate=self.rate,
                **summary,
            )
        with self._lock:
            submitted, scheduled, starved = self.submitted, self._scheduled, self.starved
        if scheduled > submitted:
            self.recorder.emit(
                "generator_shortfall",
                phase=phase,
                role=self.role.name,
                ceiling=self.ctx.ceiling,
                offered_rate=self.rate,
                scheduled=scheduled,
                submitted=submitted,
                delivered_rate=round(submitted / elapsed, 2),
            )
        if self.track_inclusion and self.watcher is not None:
            inclusions, outstanding = self.watcher.stats(self)
            self.recorder.emit(
                "inclusion",
                phase=phase,
                role=self.role.name,
                ceiling=self.ctx.ceiling,
                k_retry=self.ctx.k_retry,
                submitted=submitted,
                starved=starved,
                included=len(inclusions),
                # Still unincluded when the watcher stopped. Reported rather than folded into
                # "not included", because the two differ: one is a verdict, the other is a
                # measurement that ran out of time.
                outstanding=outstanding,
                achieved_rate=submitted / elapsed,
                inclusion_p50_ms=percentile(inclusions, 0.50),
                inclusion_p95_ms=percentile(inclusions, 0.95),
                inclusion_max_ms=round(inclusions[-1], 1) if inclusions else 0.0,
            )
        else:
            self.recorder.emit(
                "offered_load",
                phase=phase,
                role=self.role.name,
                ceiling=self.ctx.ceiling,
                submitted=submitted,
                achieved_rate=submitted / elapsed,
            )



def calibrate_generator(ctx, role, sender, offered_rate: float, recorder,
                        budget_seconds: float = 2.0, min_headroom: float = 1.5) -> bool:
    """Measures how fast this generator can build the shape it is about to offer, and refuses
    a run it cannot generate.

    A load generator that cannot build its own offered load becomes the bottleneck, and every
    number the run then produces describes the generator rather than the client. That failure
    is silent: the pacing loop still enqueues at the nominal rate and the reported rate still
    divides by the nominal window. It is caught here because the cost is shape-dependent, and
    for the signature-stuffed shape it scales with the ceiling under test, so the generator's
    own limit moves with the independent variable and reads as a finding.

    Returns False when the offered rate is beyond reach, so the caller can abort rather than
    publish a number about Python's ECDSA throughput."""
    if offered_rate <= 0:
        return True

    built = 0
    started = time.perf_counter()
    with ThreadPoolExecutor(max_workers=MAX_INFLIGHT) as pool:
        while time.perf_counter() - started < budget_seconds:
            batch = min(MAX_INFLIGHT, 16)
            futures = [
                pool.submit(role.build, ctx, sender, 0, CALIBRATION_BASE_FEE,
                            (built + index).to_bytes(8, "big"), None)
                for index in range(batch)
            ]
            for future in futures:
                future.result()
            built += batch
    elapsed = time.perf_counter() - started
    capacity = built / elapsed
    headroom = capacity / offered_rate

    recorder.emit(
        "generator_capacity",
        role=role.name,
        shape=role.shape,
        ceiling=ctx.ceiling,
        offered_rate=offered_rate,
        build_rate_max=round(capacity, 1),
        headroom=round(headroom, 2),
        sufficient="yes" if headroom >= min_headroom else "no",
    )
    return headroom >= min_headroom


def wait_for_network(ctx: Context, timeout: float) -> bool:
    """Blocks until every node answers and the chain has moved past genesis.

    Frame transactions are rejected before the Hegota fork activates, so submitting during
    the first epoch would measure the pre-fork rejection path and call it an admission
    cost."""
    for node in ctx.nodes:
        if not node.wait_until_ready(timeout):
            ctx.recorder.emit("preflight", node=node.name, client=node.client, ready=False)
            return False
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        heads = []
        for node in ctx.nodes:
            try:
                heads.append(node.block_number())
            except (RpcError, OSError):
                heads.append(0)
        if all(head > 0 for head in heads):
            for node, head in zip(ctx.nodes, heads):
                ctx.recorder.emit(
                    "preflight", node=node.name, client=node.client, ready=True, head=head
                )
            return True
        time.sleep(2.0)
    return False


def wait_for_fork_activation(ctx: Context, sender, timeout: float) -> bool:
    """Blocks until every node accepts a valid frame transaction.

    The devnet schedules Hegota at epoch 1, so for the first epoch both clients reject frame
    transactions as pre-fork. Timing anything before that measures the pre-fork rejection path
    and reports it as an admission cost. Probing with a real transaction rather than computing
    the activation timestamp keeps this client-agnostic: whatever each client's rule is, the
    gate opens when the client actually starts accepting."""
    from . import shapes

    started = time.monotonic()
    deadline = started + timeout
    pending = list(ctx.nodes)
    attempts = {node.name: 0 for node in ctx.nodes}
    last_reason: dict[str, str] = {}

    while pending and time.monotonic() < deadline:
        still_waiting = []
        for node in pending:
            attempts[node.name] += 1
            try:
                sender.sync_nonce(node)
                built = shapes.build_baseline(
                    ctx.module, sender, ctx.chain_id, sender.next_nonce(), node.base_fee(), None
                )
                node.send_raw(built.raw)
            except RpcError as error:
                last_reason[node.name] = error.message
                still_waiting.append(node)
                continue
            except OSError as error:
                last_reason[node.name] = "transport: {0}".format(error)
                still_waiting.append(node)
                continue
            ctx.recorder.emit(
                "fork_gate", node=node.name, client=node.client, active=True,
                attempts=attempts[node.name], waited_s=round(time.monotonic() - started, 1),
            )
        pending = still_waiting
        if pending:
            time.sleep(ctx.seconds_per_slot)

    for node in pending:
        ctx.recorder.emit(
            "fork_gate", node=node.name, client=node.client, active=False,
            attempts=attempts[node.name], waited_s=round(time.monotonic() - started, 1),
            reason=last_reason.get(node.name, "-"),
        )
    return not pending


def verify_ceiling_is_active(ctx: Context, sender) -> None:
    """Probes each client with a prefix just over the ceiling and records what it says.

    This is the acceptance check that the configured ceiling is live on both implementations:
    a node running a stock image accepts what a patched one refuses, and the difference shows
    up here rather than as a silently wrong measurement."""
    from . import shapes

    over = ctx.ceiling + shapes.SECP256K1_VERIFICATION_GAS * 4
    for node in ctx.nodes:
        sender.sync_nonce(node)
        built = shapes.build_signature_stuffed(
            ctx.module, sender, ctx.chain_id, sender.next_nonce(), node.base_fee(), over,
            b"\x00" * 8, None,
        )
        rejected, reason = False, ""
        try:
            node.send_raw(built.raw)
        except RpcError as error:
            rejected, reason = True, error.message
        except OSError as error:
            reason = "transport: {0}".format(error)
        ctx.recorder.emit(
            "ceiling_probe",
            node=node.name,
            client=node.client,
            ceiling=ctx.ceiling,
            probed_verify_gas=built.declared_verify_gas,
            rejected=rejected,
            reason=reason or "-",
        )
