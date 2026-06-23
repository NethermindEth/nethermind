// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.State.History
{
    /// <summary>
    /// The single stable abstraction of the archive: a versioned key→value view of historical state,
    /// derived from the immutable block log. This is the one committed decision (see
    /// <c>.agent/tasks/31-archive-node-first-principles.md</c>); every physical choice — value codec,
    /// clustering, storage engine, block-vs-txNum keying — lives behind this seam and is therefore
    /// regenerable, not a commitment.
    ///
    /// Logical model (Łukasz's draft, reth-style split):
    ///   • a position index  address → [block numbers where it changed]  (reuses <c>ILogIndexStorage</c>)
    ///   • a value store      (address‖block) → account / (address‖slot‖block) → slot value
    /// An "as-of" read is a single seek to the most-recent change ≤ block.
    /// </summary>
    public interface IStateHistory
    {
        /// <summary>
        /// Account RLP as of <paramref name="blockNumber"/> = the most recent change at or before it.
        /// Returns <c>false</c> (and null) when the account did not exist at that block — including the
        /// explicit tombstone case (self-destruct / EIP-158 emptied): a deletion marker MUST win over an
        /// older value, otherwise the seek returns a stale pre-deletion account. Decoding RLP →
        /// <c>Account</c> is the read-routing layer's job, not the value store's (Clean Arch decoupling).
        /// </summary>
        bool TryGetAccountRlp(long blockNumber, Address address, out byte[]? accountRlp);

        /// <summary>
        /// Storage slot value as of <paramref name="blockNumber"/>, leading-zero-trimmed (Łukasz's
        /// "32 bytes, trim leading 0"). Null = unset/cleared at that block (the slot tombstone).
        /// </summary>
        byte[]? GetStorage(long blockNumber, Address address, in UInt256 index);

        /// <summary>
        /// True if this store can answer reads for <paramref name="blockNumber"/> (i.e. it is within the
        /// backfilled/indexed range). The tip / recent window is still served by the trie scope; this
        /// store owns the finalized history below the (dynamic) boundary.
        /// </summary>
        bool Covers(long blockNumber);

        /// <summary>
        /// The change events produced by a single block, the unit written on the forward path and emitted
        /// by trie-diff backfill. Ingesting is append-only and idempotent per block (no reorg below the
        /// boundary — reorgs are handled by the mutable tip overlay, not here).
        /// </summary>
        void Ingest(long blockNumber, IReadOnlyList<StateChange> changes);
    }

    /// <summary>
    /// One state mutation at a block, keyed by trie hashes (keccak(address) / keccak(slot)) so it can be
    /// produced both by the forward path (hash the preimage) and by trie-diff backfill (the hashes come
    /// straight off the trie leaves — no preimage DB needed). A DTO at the boundary (Clean Arch) — no
    /// behaviour. <see cref="IsDeletion"/> carries the tombstone.
    /// </summary>
    public readonly struct StateChange
    {
        /// <summary>keccak(address) — the state-trie path of the account.</summary>
        public readonly ValueHash256 AddressHash;
        /// <summary>Null for an account-level change; keccak(slot) for a storage-slot change.</summary>
        public readonly ValueHash256? SlotHash;
        /// <summary>Account RLP / leading-zero-trimmed slot value. Empty when <see cref="IsDeletion"/>.</summary>
        public readonly ReadOnlyMemory<byte> Value;
        public readonly bool IsDeletion;

        private StateChange(in ValueHash256 addressHash, ValueHash256? slotHash, ReadOnlyMemory<byte> value, bool isDeletion)
        {
            AddressHash = addressHash;
            SlotHash = slotHash;
            Value = value;
            IsDeletion = isDeletion;
        }

        public static StateChange Account(in ValueHash256 addressHash, ReadOnlyMemory<byte> rlp) => new(addressHash, null, rlp, false);
        public static StateChange AccountDeleted(in ValueHash256 addressHash) => new(addressHash, null, default, true);
        public static StateChange Storage(in ValueHash256 addressHash, in ValueHash256 slotHash, ReadOnlyMemory<byte> trimmedValue) => new(addressHash, slotHash, trimmedValue, false);
        public static StateChange StorageCleared(in ValueHash256 addressHash, in ValueHash256 slotHash) => new(addressHash, slotHash, default, true);
    }
}
