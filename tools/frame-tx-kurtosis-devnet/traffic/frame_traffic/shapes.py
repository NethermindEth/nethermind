"""Frame-transaction shapes for the campaign's roles.

Encoding and gas accounting come from the vendored soispoke encoder (`devnet/frametx.py`),
which targets the same envelope dialect both clients implement: frames are
`[mode, flags, target, limits=[execution, state], value, data]`, mode 1 is VERIFY, flag 0x03
approves execution and payment, and the intrinsic terms are 12000 + 475/frame + 2800 per
secp256k1 signature. Those constants are asserted against the encoder at import so a dialect
drift upstream fails here rather than silently producing transactions one client rejects.
"""
from __future__ import annotations

import importlib.util
import os
import sys
from dataclasses import dataclass

# EIP-8141 frame modes and approve flags, mirroring Nethermind's TxFrame constants.
MODE_DEFAULT = 0
MODE_VERIFY = 1
MODE_SENDER = 2

APPROVE_PAYMENT = 0x1
APPROVE_EXECUTION = 0x2
APPROVE_EXECUTION_AND_PAYMENT = 0x3

EXPIRY_VERIFIER_ADDRESS = 0x8141
EXPIRY_DATA_LENGTH = 8

INTRINSIC_GAS = 12_000
PER_FRAME_GAS = 475
SECP256K1_VERIFICATION_GAS = 2_800

# Smallest execution budget a VERIFY frame is given when the point of the shape is the
# signature work rather than the frame's own execution. Mirrors the in-process harness.
MINIMAL_FRAME_GAS = 400

_frametx = None


def load_encoder(vendor_dir: str | None = None):
    """Imports the vendored encoder and checks it still speaks our dialect."""
    global _frametx
    if _frametx is not None:
        return _frametx

    candidates = []
    if vendor_dir:
        candidates.append(os.path.join(vendor_dir, "frametx.py"))
    here = os.path.dirname(os.path.abspath(__file__))
    candidates.append(os.path.join(here, "..", "vendor", "soispoke", "frametx.py"))
    candidates.append("/opt/frame-traffic/vendor/soispoke/frametx.py")

    path = next((os.path.abspath(c) for c in candidates if os.path.exists(c)), None)
    if path is None:
        raise RuntimeError(
            "the vendored frame-transaction encoder was not found. Run traffic/vendor/fetch.sh "
            "(looked in: {0})".format(", ".join(candidates))
        )

    spec = importlib.util.spec_from_file_location("frametx_vendored", path)
    module = importlib.util.module_from_spec(spec)
    sys.modules["frametx_vendored"] = module
    spec.loader.exec_module(module)

    _assert_dialect(module, path)
    _frametx = module
    return module


_compat_cache = {}


def frame_tx(module, **kwargs):
    """Builds a transaction in the envelope both clients actually decode.

    The vendored encoder always writes EIP-8250's `nonce_keys` list, but the deployed envelope
    carries it only when there are keys: Nethermind reads `nonce_keys` if the next item is a
    list and otherwise takes a scalar nonce (FrameTxDecoder.DecodePayload), and ethrex rejects
    the always-present form with "field 'nonce' of type u64: UnexpectedList". A keyless
    transaction therefore has to omit the list, not send an empty one."""
    compat = _compat_cache.get(id(module))
    if compat is None:
        base = module.FrameTx

        class FrameTxCompat(base):
            def _envelope(self, elide_sigs, field_overrides=None):
                fields = base._envelope(self, elide_sigs, field_overrides)
                if not self.nonce_keys:
                    return fields[:1] + fields[2:]
                return fields

        compat = FrameTxCompat
        _compat_cache[id(module)] = compat
    return compat(**kwargs)


def _assert_dialect(module, path: str) -> None:
    missing = [n for n in ("Frame", "FrameSig", "FrameTx") if not hasattr(module, n)]
    if missing:
        raise RuntimeError("{0} is missing {1}; the encoder's API changed".format(path, missing))
    if module.FrameSig.SECP256K1 != 1:
        raise RuntimeError("encoder secp256k1 scheme id is {0}, expected 1".format(module.FrameSig.SECP256K1))

    probe = frame_tx(
        module,
        chain_id=1,
        nonce_keys=[],
        nonce_seq=0,
        sender=0,
        frames=[module.Frame(MODE_VERIFY, APPROVE_EXECUTION_AND_PAYMENT, None, 0, 0, b"")],
        signatures=[],
        max_priority_fee=0,
        max_fee=0,
    )
    if probe.raw()[:1] != b"\x06":
        raise RuntimeError("encoder produced transaction type {0!r}, expected 0x06".format(probe.raw()[:1]))
    if probe.mandatory_gas() != INTRINSIC_GAS + PER_FRAME_GAS:
        raise RuntimeError(
            "encoder intrinsic gas is {0}, expected {1}; the fork profile moved".format(
                probe.mandatory_gas(), INTRINSIC_GAS + PER_FRAME_GAS
            )
        )

    # Envelope shape, which is where the vendored encoder and the deployed clients diverge.
    # A keyless transaction is [chain_id, nonce, sender, frames, signatures, fees,
    # blob_hashes]: seven fields, with a scalar in slot 1. Sending the empty nonce_keys list
    # shifts every later field and both clients reject it.
    fields = module.rlp_items(probe.encode())
    if len(fields) != 7:
        raise RuntimeError(
            "keyless envelope has {0} top-level fields, expected 7; the envelope dialect "
            "moved".format(len(fields))
        )
    if fields[1][0] >= 0xC0:
        raise RuntimeError(
            "envelope slot 1 is a list, but a keyless transaction must carry a scalar nonce "
            "there; the nonce_keys list is only present when there are keys"
        )


def declared_verify_gas(tx) -> int:
    """The validation-prefix gas Nethermind compares against MAX_VERIFY_GAS at admission.

    Mirrors FrameTxValidation.ValidationWorkGas: the execution limits of the frames up to the
    end of the recognised prefix, plus signature verification. The prefix starts after an
    optional leading expiry frame and an optional deploy frame, and is either one self-verify
    frame approving execution and payment, or a self-verify frame approving execution followed
    by a VERIFY frame approving payment. An unrecognised layout counts every frame."""
    frames = tx.frames
    sender = tx.sender

    def self_targeted(frame, flags):
        return frame.mode == MODE_VERIFY and frame.flags == flags and frame.target in (None, sender)

    start = 0
    if (start < len(frames) and frames[start].mode == MODE_VERIFY and frames[start].flags == 0
            and frames[start].target == EXPIRY_VERIFIER_ADDRESS and frames[start].value == 0
            and len(frames[start].data) == EXPIRY_DATA_LENGTH):
        start += 1
    if start < len(frames) and frames[start].mode == MODE_DEFAULT and frames[start].flags == 0:
        start += 1

    counted = len(frames)
    if start < len(frames) and self_targeted(frames[start], APPROVE_EXECUTION_AND_PAYMENT):
        counted = start + 1
    elif (start + 1 < len(frames) and self_targeted(frames[start], APPROVE_EXECUTION)
          and frames[start + 1].mode == MODE_VERIFY and frames[start + 1].flags == APPROVE_PAYMENT):
        counted = start + 2

    return sum(frame.gas_limit for frame in frames[:counted]) + tx.signature_verification_cost()


# ---------------------------------------------------------------------------------------
# EVM fixtures
# ---------------------------------------------------------------------------------------

# JUMPDEST; PUSH2 0x1000; PUSH1 0; KECCAK256; POP; PUSH1 0; JUMP
# KECCAK256 over 4 KB per iteration, forever: the plan's CPU-heavy adversarial baseline. The
# frame's own gas limit is what stops it, so the burn tracks the ceiling exactly.
KECCAK_WIDE_RUNTIME = bytes.fromhex("5b61100060002050600056")


def deployment_init_code(runtime: bytes) -> bytes:
    """Wraps runtime code in a minimal constructor that returns it.

    Artifacts like the Groth16 verifier are published as runtime code, not as deployment code:
    sending them to an empty `to` executes them as a constructor and reverts. They have to be
    wrapped. PUSH1 only reaches 255 bytes, so anything larger needs the PUSH2 form."""
    size = len(runtime)
    if size > 0xFFFF:
        raise ValueError("runtime longer than this minimal constructor supports")
    if size <= 0xFF:
        push_len = bytes([0x60, size])     # PUSH1 size
        offset = 12
        push_off = bytes([0x60, offset])
    else:
        push_len = bytes([0x61]) + size.to_bytes(2, "big")   # PUSH2 size
        offset = 14
        push_off = bytes([0x60, offset])
    return (
        push_len                           # size, for CODECOPY
        + push_off                         # offset of runtime within this init code
        + bytes([0x60, 0x00])              # destOffset 0
        + bytes([0x39])                    # CODECOPY
        + push_len                         # size, for RETURN
        + bytes([0x60, 0x00])              # offset 0
        + bytes([0xF3])                    # RETURN
        + runtime
    )


@dataclass
class BuiltTx:
    raw: bytes
    shape: str
    declared_verify_gas: int
    frames: int
    signatures: int
    expiry_deadline: int | None


def _expiry_frame(module, deadline_unix: int):
    """Leading expiry frame. Its deadline is how K_retry is expressed on the wire: neither
    client implements a retry counter, but an expiry deadline N slots out bounds a pending
    transaction to at most N build attempts before the pool must evict it."""
    return module.Frame(
        MODE_VERIFY,
        0,
        EXPIRY_VERIFIER_ADDRESS,
        MINIMAL_FRAME_GAS,
        0,
        deadline_unix.to_bytes(EXPIRY_DATA_LENGTH, "big"),
    )


def _targeted_prefix(module, target: bytes, budget: int, data: bytes):
    """Two-frame validation prefix whose heavy frame targets a contract.

    A single frame cannot both approve execution and target something other than the sender:
    Nethermind rejects that outright with "frames allowed to approve execution must target the
    sender". The recognised layout for this is the pair `declared_verify_gas` accepts as
    `flags == [0x02, 0x01]`: approve execution from the sender, then approve payment from the
    frame that does the work."""
    return [
        module.Frame(MODE_VERIFY, APPROVE_EXECUTION, None, MINIMAL_FRAME_GAS, 0, b""),
        module.Frame(MODE_VERIFY, APPROVE_PAYMENT, int.from_bytes(target, "big"), budget, 0, data),
    ]


def _finalise(module, tx, sender) -> bytes:
    """Signs the transaction with the sender's self-signature.

    The self-signature carries an empty `msg`, which the encoder elides from `sig_hash`, so
    the digest can be taken before the signature exists."""
    digest = tx.sig_hash()
    tx.signatures[0].signature = sender.sign_hash(digest)
    return tx.raw()


def _base_signature(module, sender):
    return module.FrameSig(module.FrameSig.SECP256K1, sender.address_bytes, b"", b"\x00" * 65)


def _fees(base_fee: int) -> tuple[int, int]:
    priority = 1_000_000_000
    return priority, max(base_fee * 2, 0) + priority


def build_baseline(module, sender, chain_id: int, nonce: int, base_fee: int,
                   deadline_unix: int | None, payload_gas: int = 30_000) -> BuiltTx:
    """A valid self-verifying transfer that must reach a block.

    Prefix is a single VERIFY frame approving both execution and payment, the shape
    `declared_verify_gas` recognises as `flags == 0x03`."""
    frames = []
    if deadline_unix is not None:
        frames.append(_expiry_frame(module, deadline_unix))
    frames.append(module.Frame(MODE_VERIFY, APPROVE_EXECUTION_AND_PAYMENT, None, MINIMAL_FRAME_GAS, 0, b""))
    frames.append(module.Frame(MODE_DEFAULT, 0, int.from_bytes(sender.address_bytes, "big"), payload_gas, 0, b""))

    priority, max_fee = _fees(base_fee)
    tx = frame_tx(
        module,
        chain_id=chain_id,
        nonce_keys=[],
        nonce_seq=nonce,
        sender=int.from_bytes(sender.address_bytes, "big"),
        frames=frames,
        signatures=[_base_signature(module, sender)],
        max_priority_fee=priority,
        max_fee=max_fee,
    )
    raw = _finalise(module, tx, sender)
    return BuiltTx(raw, "baseline", declared_verify_gas(tx), len(frames), 1, deadline_unix)


def build_keccak_wide(module, sender, chain_id: int, nonce: int, base_fee: int, ceiling: int,
                      loop_address: bytes, salt: bytes, deadline_unix: int | None) -> BuiltTx:
    """A VERIFY frame that burns the whole ceiling in KECCAK256 and never approves.

    The frame's execution budget is the ceiling minus the signature verification the same
    transaction already owes, so the declared validation gas lands just under the limit and
    the prefix is rejected only after the work is done."""
    frames = []
    reserved = SECP256K1_VERIFICATION_GAS + MINIMAL_FRAME_GAS
    if deadline_unix is not None:
        frames.append(_expiry_frame(module, deadline_unix))
        reserved += MINIMAL_FRAME_GAS
    budget = ceiling - reserved
    if budget <= 0:
        raise ValueError("ceiling {0} is too small to carry a keccak-wide prefix".format(ceiling))

    frames.extend(_targeted_prefix(module, loop_address, budget, salt))

    priority, max_fee = _fees(base_fee)
    tx = frame_tx(
        module,
        chain_id=chain_id,
        nonce_keys=[],
        nonce_seq=nonce,
        sender=int.from_bytes(sender.address_bytes, "big"),
        frames=frames,
        signatures=[_base_signature(module, sender)],
        max_priority_fee=priority,
        max_fee=max_fee,
    )
    raw = _finalise(module, tx, sender)
    return BuiltTx(raw, "keccak-wide", declared_verify_gas(tx), len(frames), 1, deadline_unix)


def stuffed_signature_count(ceiling: int) -> int:
    """Total secp256k1 entries that fit under the ceiling once the VERIFY frame is reserved.

    This is the count of *all* entries including the sender's own self-signature, matching the
    in-process harness: at 235800 it is 84 entries for 235600 declared gas."""
    return max(2, (ceiling - MINIMAL_FRAME_GAS) // SECP256K1_VERIFICATION_GAS)


def build_signature_stuffed(module, sender, chain_id: int, nonce: int, base_fee: int,
                            ceiling: int, salt: bytes, deadline_unix: int | None) -> BuiltTx:
    """Signature entries padded to the ceiling, the last one unverifiable.

    This never reaches the EVM: the node pays for every recovery up to the failing entry and
    rejects on declared gas and signature verification alone."""
    count = stuffed_signature_count(ceiling)

    frames = []
    if deadline_unix is not None:
        frames.append(_expiry_frame(module, deadline_unix))
    frames.append(module.Frame(MODE_VERIFY, APPROVE_EXECUTION_AND_PAYMENT, None, MINIMAL_FRAME_GAS, 0, b""))

    # The sender's own self-signature and the failing entry are both part of the budget, so
    # only count - 2 padding entries fit.
    signatures = [_base_signature(module, sender)]
    for index in range(count - 2):
        # Never all-zero: Nethermind rejects a signature entry whose msg is the zero digest.
        message = (b"\x5f" + salt + index.to_bytes(4, "big")).ljust(32, b"\x5f")[:32]
        signatures.append(
            module.FrameSig(
                module.FrameSig.SECP256K1, sender.address_bytes, message, sender.sign_hash(message)
            )
        )
    # The last entry signs a different message than it carries, so recovery yields the wrong
    # signer and the transaction is refused after every earlier recovery has been paid for.
    mismatched = (b"\x5f" + salt + b"\xff\xff\xff\xff").ljust(32, b"\x5f")[:32]
    signatures.append(
        module.FrameSig(
            module.FrameSig.SECP256K1,
            sender.address_bytes,
            mismatched,
            # Signs a different, non-zero digest, so recovery yields the wrong signer without
            # tripping the zero-digest rule.
            sender.sign_hash((b"\x5e" + salt).ljust(32, b"\x5e")[:32]),
        )
    )

    priority, max_fee = _fees(base_fee)
    tx = frame_tx(
        module,
        chain_id=chain_id,
        nonce_keys=[],
        nonce_seq=nonce,
        sender=int.from_bytes(sender.address_bytes, "big"),
        frames=frames,
        signatures=signatures,
        max_priority_fee=priority,
        max_fee=max_fee,
    )
    raw = _finalise(module, tx, sender)
    return BuiltTx(
        raw, "signature-stuffed", declared_verify_gas(tx), len(frames), len(signatures), deadline_unix
    )


def build_groth16(module, sender, chain_id: int, nonce: int, base_fee: int, ceiling: int,
                  verifier_address: bytes, calldata: bytes, salt: bytes,
                  deadline_unix: int | None, shape: str) -> BuiltTx:
    """A VERIFY frame calling a real Groth16 verifier.

    With the invalid-proof calldata this is the privacy-shaped attack: the pairing runs in
    full and the prefix fails only afterwards. With valid calldata the same shape is the
    privacy transaction the ceiling exists to admit."""
    frames = []
    reserved = SECP256K1_VERIFICATION_GAS + MINIMAL_FRAME_GAS
    if deadline_unix is not None:
        frames.append(_expiry_frame(module, deadline_unix))
        reserved += MINIMAL_FRAME_GAS
    budget = ceiling - reserved
    if budget <= 0:
        raise ValueError("ceiling {0} is too small to carry a Groth16 prefix".format(ceiling))

    frames.extend(_targeted_prefix(module, verifier_address, budget, calldata + salt))

    priority, max_fee = _fees(base_fee)
    tx = frame_tx(
        module,
        chain_id=chain_id,
        nonce_keys=[],
        nonce_seq=nonce,
        sender=int.from_bytes(sender.address_bytes, "big"),
        frames=frames,
        signatures=[_base_signature(module, sender)],
        max_priority_fee=priority,
        max_fee=max_fee,
    )
    raw = _finalise(module, tx, sender)
    return BuiltTx(raw, shape, declared_verify_gas(tx), len(frames), 1, deadline_unix)
