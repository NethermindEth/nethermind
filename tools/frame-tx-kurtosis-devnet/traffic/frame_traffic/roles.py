"""Traffic roles: one baseline and three attacker shapes, plus the privacy-inclusion probe."""
from __future__ import annotations

import os
import time
from dataclasses import dataclass, field

from . import shapes
from .rpc import RpcClient, RpcError

GROTH16_SWEEP_BY_CEILING = {
    235800: "sweep-soispoke",
    250000: "sweep-250k",
    300000: "sweep-300k",
    400000: "sweep-400k",
    500000: "sweep-500k",
}
GROTH16_DEFAULT_SWEEP = "sweep-soispoke"


@dataclass
class Context:
    module: object
    chain_id: int
    ceiling: int
    k_retry: int
    seconds_per_slot: int
    nodes: list[RpcClient]
    recorder: object
    groth16_artifacts: str = ""
    privacy_calldata_path: str = ""
    fixtures: dict = field(default_factory=dict)

    @property
    def submit_node(self) -> RpcClient:
        return self.nodes[0]

    def deadline(self) -> int | None:
        """Wall-clock deadline K_retry slots out.

        Neither client implements a retry counter, so this is how the sweep's K_retry axis
        reaches the network: an expiry deadline N slots ahead bounds a pending transaction to
        at most N block-building attempts before the pool has to evict it. K_retry=1 with a
        one-slot deadline is the aggressive-eviction end of the plan's sweep."""
        if self.k_retry <= 0:
            return None
        latest = self.submit_node.get_block("latest")
        now = int(latest["timestamp"], 16) if latest else int(time.time())
        return now + self.k_retry * self.seconds_per_slot


def _deploy(ctx: Context, sender, init_code: bytes, label: str) -> bytes:
    node = ctx.submit_node
    nonce = node.nonce(sender.address, "pending")
    base_fee = node.base_fee()
    tx = {
        "type": 2,
        "chainId": ctx.chain_id,
        "nonce": nonce,
        "gas": 2_000_000,
        "maxFeePerGas": max(base_fee * 2, 1) + 1_000_000_000,
        "maxPriorityFeePerGas": 1_000_000_000,
        "value": 0,
        "data": "0x" + init_code.hex(),
    }
    tx_hash = node.send_raw(sender.sign_legacy_typed_tx(tx))
    receipt = node.wait_for_receipt(tx_hash, timeout=120.0)
    if receipt is None:
        raise RuntimeError("{0} deployment {1} was not mined within 120s".format(label, tx_hash))
    if int(receipt.get("status", "0x0"), 16) != 1:
        raise RuntimeError("{0} deployment {1} reverted".format(label, tx_hash))
    address = receipt["contractAddress"]
    ctx.recorder.emit(
        "fixture_deployed", fixture=label, address=address, block=int(receipt["blockNumber"], 16)
    )
    return bytes.fromhex(address[2:])


def _read_hex_artifact(root: str, sweep: str, filename: str) -> bytes:
    path = os.path.join(root, sweep, filename)
    if not os.path.exists(path):
        raise FileNotFoundError(path)
    with open(path) as handle:
        text = handle.read().strip()
    if text.startswith("0x"):
        text = text[2:]
    return bytes.fromhex(text)


class Role:
    name = "role"
    shape = "role"
    # No role carries an expiry deadline on this devnet. K_retry bounds producer-side
    # re-execution of transactions that are already pending, but every attacker shape here is
    # refused at admission and never enters the pool, so there is no retry to bound. Attaching
    # a deadline anyway made transactions arrive already expired: the expiry frame then
    # reverted before the heavy frame ran, which flattened the measured cost across every
    # ceiling and destroyed the signal. K_retry stays a closed-form quantity, as the
    # in-process harness also treats it (k_basis=modelled).
    uses_deadline = False

    def prepare(self, ctx: Context, sender) -> None:
        return None

    def build(self, ctx: Context, sender, nonce: int, base_fee: int, salt: bytes,
              deadline: int | None):
        raise NotImplementedError


class BaselineRole(Role):
    name = "baseline"
    shape = "baseline"
    uses_deadline = False

    def build(self, ctx, sender, nonce, base_fee, salt, deadline):
        return shapes.build_baseline(ctx.module, sender, ctx.chain_id, nonce, base_fee, deadline)


class KeccakWideRole(Role):
    name = "keccak-wide"
    shape = "keccak-wide"

    def prepare(self, ctx, sender):
        if "keccak_loop" not in ctx.fixtures:
            ctx.fixtures["keccak_loop"] = _deploy(
                ctx, sender, shapes.deployment_init_code(shapes.KECCAK_WIDE_RUNTIME), "keccak-wide-loop"
            )

    def build(self, ctx, sender, nonce, base_fee, salt, deadline):
        return shapes.build_keccak_wide(
            ctx.module, sender, ctx.chain_id, nonce, base_fee, ctx.ceiling,
            ctx.fixtures["keccak_loop"], salt, deadline,
        )


class SignatureStuffedRole(Role):
    name = "signature-stuffed"
    shape = "signature-stuffed"

    def build(self, ctx, sender, nonce, base_fee, salt, deadline):
        return shapes.build_signature_stuffed(
            ctx.module, sender, ctx.chain_id, nonce, base_fee, ctx.ceiling, salt, deadline
        )


class SoispokeGroth16Role(Role):
    """Floods with the real shielded-pool verifier and a real invalid proof.

    The proof is invalid on purpose: this is the attacker role, and the cost it measures is
    the pairing the node completes before it can tell. Valid privacy transactions are the
    separate inclusion probe below."""

    name = "soispoke-groth16"
    shape = "groth16-soispoke"

    def prepare(self, ctx, sender):
        if not ctx.groth16_artifacts:
            raise RuntimeError(
                "the soispoke-groth16 role needs --groth16-artifacts pointing at a "
                "NethermindEth/frame-verify-gas sweep tree (verifier.hex, calldata-invalid.hex)"
            )
        sweep = GROTH16_SWEEP_BY_CEILING.get(ctx.ceiling, GROTH16_DEFAULT_SWEEP)
        try:
            verifier = _read_hex_artifact(ctx.groth16_artifacts, sweep, "verifier.hex")
            calldata = _read_hex_artifact(ctx.groth16_artifacts, sweep, "calldata-invalid.hex")
        except FileNotFoundError as error:
            raise RuntimeError(
                "Groth16 artifact {0} is missing; build it with the artifacts tree's "
                "generate.sh or point --groth16-artifacts at a tree that has it".format(error)
            )
        ctx.fixtures["groth16_sweep"] = sweep
        ctx.fixtures["groth16_calldata"] = calldata
        if "groth16_verifier" not in ctx.fixtures:
            # verifier.hex is runtime code, so it needs the deployment wrapper.
            ctx.fixtures["groth16_verifier"] = _deploy(
                ctx, sender, shapes.deployment_init_code(verifier),
                "groth16-verifier-{0}".format(sweep)
            )

    def build(self, ctx, sender, nonce, base_fee, salt, deadline):
        return shapes.build_groth16(
            ctx.module, sender, ctx.chain_id, nonce, base_fee, ctx.ceiling,
            ctx.fixtures["groth16_verifier"], ctx.fixtures["groth16_calldata"], salt, deadline,
            self.shape,
        )


ATTACKER_ROLES = {
    "keccak-wide": KeccakWideRole,
    "signature-stuffed": SignatureStuffedRole,
    "soispoke-groth16": SoispokeGroth16Role,
}


class PrivacyInclusionProbe:
    """Answers the campaign's utility question directly: under this attack load, at this
    ceiling, does a valid privacy transaction still get admitted and included?

    It refuses to answer with a synthetic stand-in. Without real valid proof material it
    reports why, so a run can never quietly claim privacy transfers work when what was
    measured was a gas-equivalent placeholder."""

    def __init__(self, calldata_path: str):
        self.calldata_path = calldata_path

    def available(self) -> tuple[bool, str]:
        if not self.calldata_path:
            return False, "no valid-proof calldata supplied (--privacy-valid-calldata)"
        if not os.path.exists(self.calldata_path):
            return False, "valid-proof calldata {0} not found".format(self.calldata_path)
        return True, ""

    def run(self, ctx: Context, sender, attempts: int, interval: float) -> None:
        ok, reason = self.available()
        if not ok:
            ctx.recorder.emit(
                "privacy_inclusion", ceiling=ctx.ceiling, k_retry=ctx.k_retry,
                available=False, reason=reason,
            )
            return
        if "groth16_verifier" not in ctx.fixtures:
            ctx.recorder.emit(
                "privacy_inclusion", ceiling=ctx.ceiling, k_retry=ctx.k_retry, available=False,
                reason="no verifier deployed; run with the soispoke-groth16 role or supply one",
            )
            return

        with open(self.calldata_path) as handle:
            text = handle.read().strip()
        calldata = bytes.fromhex(text[2:] if text.startswith("0x") else text)

        node = ctx.submit_node
        sender.sync_nonce(node)
        for attempt in range(attempts):
            nonce = sender.next_nonce()
            base_fee = node.base_fee()
            deadline = ctx.deadline()
            built = shapes.build_groth16(
                ctx.module, sender, ctx.chain_id, nonce, base_fee, ctx.ceiling,
                ctx.fixtures["groth16_verifier"], calldata, b"", deadline, "groth16-soispoke-valid",
            )
            submitted_at = time.monotonic()
            tx_hash, admitted, reason = "", False, ""
            try:
                tx_hash = node.send_raw(built.raw)
                admitted = True
            except RpcError as error:
                reason = error.message
            except OSError as error:
                reason = "transport: {0}".format(error)

            included_block, latency_ms = None, None
            if admitted:
                receipt = node.wait_for_receipt(tx_hash, timeout=ctx.seconds_per_slot * 8)
                if receipt is not None:
                    included_block = int(receipt["blockNumber"], 16)
                    latency_ms = (time.monotonic() - submitted_at) * 1000.0
                    if int(receipt.get("status", "0x0"), 16) != 1:
                        reason = "included but reverted"
                else:
                    reason = "admitted but not included within {0}s".format(ctx.seconds_per_slot * 8)

            ctx.recorder.emit(
                "privacy_inclusion",
                ceiling=ctx.ceiling,
                k_retry=ctx.k_retry,
                available=True,
                attempt=attempt,
                node=node.name,
                client=node.client,
                declared_verify_gas=built.declared_verify_gas,
                admitted=admitted,
                tx_hash=tx_hash or "-",
                included_block=included_block if included_block is not None else "-",
                inclusion_latency_ms=latency_ms if latency_ms is not None else "-",
                reason=reason or "-",
            )
            if attempt + 1 < attempts:
                time.sleep(interval)
