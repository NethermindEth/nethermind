"""Sender accounts and nonce bookkeeping.

The devnet's prefunded accounts arrive from ethereum-package's own output, so no mnemonic
derivation happens here. Each role takes its own account: a shared account would serialise
every role behind one nonce sequence and turn an offered-rate sweep into a queueing
experiment."""
from __future__ import annotations

import threading

from eth_account import Account
from eth_keys import keys
from eth_utils import to_checksum_address


class Sender:
    """One account, with a locally tracked sequence nonce.

    Frame transactions carry `nonce_seq` in the envelope rather than a classic nonce field,
    but the pool still orders by it, so it is tracked the same way. The local counter avoids
    an `eth_getTransactionCount` round trip per submission, which at a few hundred
    transactions per second would dominate the measured admission latency."""

    def __init__(self, address: str, private_key: str):
        self.address = to_checksum_address(address)
        key = private_key[2:] if private_key.startswith("0x") else private_key
        self.private_key = bytes.fromhex(key)
        self._signing_key = keys.PrivateKey(self.private_key)
        self._nonce = 0
        self._lock = threading.Lock()

    @property
    def address_bytes(self) -> bytes:
        return bytes.fromhex(self.address[2:])

    def sync_nonce(self, rpc) -> int:
        with self._lock:
            self._nonce = rpc.nonce(self.address, "pending")
            return self._nonce

    def next_nonce(self) -> int:
        with self._lock:
            value = self._nonce
            self._nonce += 1
            return value

    def peek_nonce(self) -> int:
        with self._lock:
            return self._nonce

    def sign_hash(self, digest: bytes) -> bytes:
        """65-byte [recovery_id || r || s], the layout EIP-8141 secp256k1 signature entries
        use (recovery id leading, not trailing as in a legacy transaction signature)."""
        signature = self._signing_key.sign_msg_hash(digest)
        return (
            bytes([signature.v])
            + signature.r.to_bytes(32, "big")
            + signature.s.to_bytes(32, "big")
        )

    def sign_legacy_typed_tx(self, tx: dict) -> bytes:
        """Signs an ordinary EIP-1559 transaction, used only for deploying the fixtures the
        attacker roles need (the keccak loop, the Groth16 verifier)."""
        signed = Account.sign_transaction(tx, self.private_key)
        return bytes(signed.raw_transaction)

    def __repr__(self) -> str:
        return "Sender({0})".format(self.address)


def parse_accounts(specs: list[str]) -> list[Sender]:
    senders = []
    for spec in specs:
        if ":" not in spec:
            raise ValueError("account '{0}' is not <address>:<private_key>".format(spec))
        address, private_key = spec.split(":", 1)
        senders.append(Sender(address.strip(), private_key.strip()))
    return senders


class AccountPool:
    """Hands each role a distinct account, and fails loudly rather than silently sharing."""

    def __init__(self, senders: list[Sender]):
        if not senders:
            raise ValueError("no prefunded accounts were supplied")
        self._senders = list(senders)
        self._assigned: dict[str, Sender] = {}
        self._next = 0
        self._lock = threading.Lock()

    def assign(self, role: str) -> Sender:
        with self._lock:
            if role in self._assigned:
                return self._assigned[role]
            if self._next >= len(self._senders):
                raise RuntimeError(
                    "out of prefunded accounts: {0} roles need one each, {1} available".format(
                        len(self._assigned) + 1, len(self._senders)
                    )
                )
            sender = self._senders[self._next]
            self._next += 1
            self._assigned[role] = sender
            return sender

    def assigned(self) -> dict[str, Sender]:
        with self._lock:
            return dict(self._assigned)
