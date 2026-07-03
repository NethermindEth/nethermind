// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.StateDiffArchive.Data;

/// <summary>
/// Accumulates one block's write batches — each teed from a distinct world-state <c>StartWriteBatch</c>
/// flush — plus the code captured during the block, and serializes them to the RLP wire format consumed by
/// <see cref="StateDiffRecord"/>.
/// </summary>
/// <remarks>
/// The world state opens a fresh write batch on every <c>Commit(commitRoots: true)</c>: once per block
/// post-Byzantium, but once per transaction pre-Byzantium (to produce intermediate receipt roots). Each such
/// flush is recorded as its own ordered <see cref="BatchBuilder"/> rather than merged into a net diff, so
/// replay reproduces the exact mutation sequence — in particular the repeated writes to one slot across the
/// transactions of an early block, which a merged diff would have to collapse. Code is content-addressed, so
/// it is deduped into a single block-level list regardless of which batch inserted it.
///
/// Wire format (positional; the leading version byte allows forward-compatible additions):
/// <code>
/// StateDiffRecord = [
///   Version     (byte),
///   BlockNumber (uint64),
///   StateRoot   (32B),
///   Batches = [ Batch, ... ],
///   Codes   = [ [ CodeHash (32B), Code (bytes) ], ... ]
/// ]
/// Batch = [
///   [ Address (20B), Change (byte 0|1|2), Account (RLP account, only when Change==Set),
///     StorageCleared (bool), Slots = [ [ Index (uint256), Value (bytes) ], ... ] ],
///   ...
/// ]
/// </code>
/// </remarks>
public sealed class StateDiffRecordBuilder
{
    private readonly List<BatchBuilder> _batches = [];
    private readonly Dictionary<ValueHash256, byte[]> _codes = [];

    /// <summary>Begins a new write batch; the account/storage writes teed into it stay ordered after prior batches.</summary>
    public BatchBuilder StartBatch()
    {
        BatchBuilder batch = new();
        _batches.Add(batch);
        return batch;
    }

    public void AddCode(in ValueHash256 codeHash, byte[] code) => _codes[codeHash] = code;

    public void Reset()
    {
        _batches.Clear();
        _codes.Clear();
    }

    public int GetLength(ulong blockNumber, Hash256 stateRoot) => Rlp.LengthOfSequence(GetContentLength(blockNumber, stateRoot));

    public void WriteTo<TWriter>(ref TWriter w, ulong blockNumber, Hash256 stateRoot)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        w.StartSequence(GetContentLength(blockNumber, stateRoot));
        w.Encode(StateDiffRecord.CurrentVersion);
        w.Encode(blockNumber);
        w.Encode(stateRoot);

        w.StartSequence(GetBatchesContentLength());
        foreach (BatchBuilder batch in _batches)
        {
            w.StartSequence(batch.GetContentLength());
            batch.WriteAccounts(ref w);
        }

        w.StartSequence(GetCodesContentLength());
        foreach ((ValueHash256 hash, byte[] code) in _codes)
        {
            w.StartSequence(GetCodeContentLength(code));
            w.Encode(hash);
            w.Encode(code);
        }
    }

    private int GetContentLength(ulong blockNumber, Hash256 stateRoot)
        => Rlp.LengthOf(StateDiffRecord.CurrentVersion)
           + Rlp.LengthOf(blockNumber)
           + Rlp.LengthOf(stateRoot)
           + Rlp.LengthOfSequence(GetBatchesContentLength())
           + Rlp.LengthOfSequence(GetCodesContentLength());

    private int GetBatchesContentLength()
    {
        int total = 0;
        foreach (BatchBuilder batch in _batches) total += Rlp.LengthOfSequence(batch.GetContentLength());
        return total;
    }

    private int GetCodesContentLength()
    {
        int total = 0;
        foreach ((ValueHash256 _, byte[] code) in _codes) total += Rlp.LengthOfSequence(GetCodeContentLength(code));
        return total;
    }

    private static int GetCodeContentLength(byte[] code) => Rlp.LengthOfKeccakRlp + Rlp.LengthOf(code);

    /// <summary>
    /// Accumulates the net account/storage writes of a single world-state <c>StartWriteBatch</c> flush,
    /// grouped by address.
    /// </summary>
    /// <remarks>
    /// The world state flushes per-contract storage in parallel (<c>PersistentStorageProvider.UpdateRootHashesMultiThread</c>),
    /// so the address map is a <see cref="ConcurrentDictionary{TKey,TValue}"/>. Each address is touched by a single
    /// thread within a phase (one storage write batch per contract; the account phase runs after the storage join),
    /// so a per-entry lock is unnecessary. Within a flush a slot is normally written once; the last-write-wins map
    /// and the clear-drops-earlier-slots rule only matter when a self-destruct and a re-set land in the same flush.
    /// </remarks>
    public sealed class BatchBuilder
    {
        private static readonly AccountDecoder AccountRlp = AccountDecoder.Instance;

        private readonly ConcurrentDictionary<Address, Entry> _accounts = new();

        public void SetAccount(Address address, Account? account)
        {
            Entry entry = GetOrAdd(address);
            entry.Change = account is null ? AccountChangeKind.Deleted : AccountChangeKind.Set;
            entry.Account = account;
        }

        public void ClearStorage(Address address)
        {
            Entry entry = GetOrAdd(address);
            entry.StorageCleared = true;
            entry.Slots?.Clear(); // a self-destruct wipes the slots written before it this flush
        }

        public void SetSlot(Address address, in UInt256 index, byte[] value)
            => (GetOrAdd(address).Slots ??= [])[index] = value; // last write of a slot wins

        internal int GetContentLength()
        {
            int total = 0;
            foreach ((Address _, Entry entry) in _accounts) total += Rlp.LengthOfSequence(GetAccountDiffContentLength(entry));
            return total;
        }

        internal void WriteAccounts<TWriter>(ref TWriter w)
            where TWriter : struct, IRlpWriteBackend, allows ref struct
        {
            foreach ((Address address, Entry entry) in _accounts)
            {
                w.StartSequence(GetAccountDiffContentLength(entry));
                w.Encode(address);
                w.Encode((byte)entry.Change);
                if (entry.Change == AccountChangeKind.Set) AccountRlp.Encode(ref w, entry.Account);
                w.Encode(entry.StorageCleared);

                w.StartSequence(GetSlotsContentLength(entry.Slots));
                if (entry.Slots is not null)
                {
                    foreach ((UInt256 index, byte[] value) in entry.Slots)
                    {
                        w.StartSequence(GetSlotContentLength(index, value));
                        w.Encode(index);
                        w.Encode(value);
                    }
                }
            }
        }

        private static int GetAccountDiffContentLength(Entry entry)
        {
            int length = Rlp.LengthOf(Address.Zero)
                         + Rlp.LengthOf((byte)entry.Change)
                         + Rlp.LengthOf((byte)(entry.StorageCleared ? 1 : 0))
                         + Rlp.LengthOfSequence(GetSlotsContentLength(entry.Slots));
            if (entry.Change == AccountChangeKind.Set) length += AccountRlp.GetLength(entry.Account);
            return length;
        }

        private static int GetSlotsContentLength(Dictionary<UInt256, byte[]>? slots)
        {
            if (slots is null) return 0;
            int total = 0;
            foreach ((UInt256 index, byte[] value) in slots) total += Rlp.LengthOfSequence(GetSlotContentLength(index, value));
            return total;
        }

        private static int GetSlotContentLength(in UInt256 index, byte[] value) => Rlp.LengthOf(index) + Rlp.LengthOf(value);

        private Entry GetOrAdd(Address address) => _accounts.GetOrAdd(address, static _ => new Entry());

        private sealed class Entry
        {
            public AccountChangeKind Change = AccountChangeKind.None;
            public Account? Account;
            public bool StorageCleared;
            public Dictionary<UInt256, byte[]>? Slots;
        }
    }
}
