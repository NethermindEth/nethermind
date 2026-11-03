// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Paprika;
using Paprika.Store;

namespace Nethermind.State.Flat.Persistence.PaprikaFlat;

/// <summary>
/// The flat writes and markers of one <see cref="Db.FlatLayout.PaprikaFlat"/> write batch, held until the batch commits:
/// Paprika has one writer, while sync and import keep many write batches open at once.
/// </summary>
/// <remarks>
/// <para>
/// Used by one thread at a time. Range operations are kept as a list; account and slot writes as records
/// <c>kind:u8 | account:20 | [slot:32] | length:u8 | value</c> in pooled chunks, where an empty value removes the key.
/// </para>
/// <para>
/// <see cref="ApplyTo"/> applies the buffers of a group commit in phases that read the root the Paprika batch was begun on:
/// every buffer's structural drops (whole storages, and the blocks the other range deletes cover), then every buffer's
/// key-by-key range deletes, then every buffer's writes in order. So a range delete never removes a write of the group: a
/// write issued before a range delete that covers its key survives, where the RocksDB layouts would delete it; every
/// caller issues its deletes first. The markers in <see cref="Metadata"/> are left to the caller, which merges every
/// buffer's into one blob.
/// </para>
/// </remarks>
internal sealed class PaprikaWriteBuffer(StateId from, StateId to) : IDisposable
{
    private const int ChunkSize = 64 * 1024;
    private const int SlotLength = 32;

    private readonly List<RangeOperation> _ranges = [];
    private readonly List<ArrayPoolList<byte>> _chunks = [];

    /// <summary>The state the batch writes on.</summary>
    public StateId From => from;

    /// <summary>The state the batch moves to.</summary>
    public StateId To => to;

    /// <summary>The markers the batch writes, for the trie node log and the state pointer.</summary>
    public PaprikaFlatMetadata.ChangeSet Metadata { get; } = new();

    /// <summary>Whether the batch writes nothing.</summary>
    public bool IsEmpty => _ranges.Count == 0 && _chunks.Count == 0 && Metadata.Changes.Count == 0;

    /// <summary>Writes the stored Slim RLP of the account at <paramref name="account"/>.</summary>
    public void SetAccount(in ValueHash256 account, ReadOnlySpan<byte> value) => AddRecord(RecordKind.Account, account, default, value);

    /// <summary>Removes the account at <paramref name="account"/>.</summary>
    public void RemoveAccount(in ValueHash256 account) => AddRecord(RecordKind.Account, account, default, default);

    /// <summary>Writes the stored, RLP-wrapped, value of <paramref name="slot"/> of <paramref name="account"/>.</summary>
    public void SetSlot(in ValueHash256 account, in ValueHash256 slot, ReadOnlySpan<byte> value) => AddRecord(RecordKind.Slot, account, slot.Bytes, value);

    /// <summary>Removes <paramref name="slot"/> of <paramref name="account"/>.</summary>
    public void RemoveSlot(in ValueHash256 account, in ValueHash256 slot) => AddRecord(RecordKind.Slot, account, slot.Bytes, default);

    /// <summary>Drops the storage of <paramref name="account"/> as it is before the batch; the account itself is kept.</summary>
    public void SelfDestruct(in ValueHash256 account) => _ranges.Add(new(RangeKind.SelfDestruct, account, default, default));

    /// <summary>Drops the storage, as it is before the batch, of every contract whose path is in <c>[<paramref name="fromPath"/>, <paramref name="toPath"/>]</c>.</summary>
    public void SelfDestructRange(in ValueHash256 fromPath, in ValueHash256 toPath) => _ranges.Add(new(RangeKind.SelfDestructRange, default, fromPath, toPath));

    /// <summary>Deletes the accounts, as they are before the batch, whose path is in <c>[<paramref name="fromPath"/>, <paramref name="toPath"/>]</c>.</summary>
    public void DeleteAccountRange(in ValueHash256 fromPath, in ValueHash256 toPath) => _ranges.Add(new(RangeKind.DeleteAccountRange, default, fromPath, toPath));

    /// <summary>Deletes the slots, as they are before the batch, in <c>[<paramref name="fromPath"/>, <paramref name="toPath"/>]</c> of the storage of <paramref name="account"/>.</summary>
    public void DeleteStorageRange(in ValueHash256 account, in ValueHash256 fromPath, in ValueHash256 toPath) => _ranges.Add(new(RangeKind.DeleteStorageRange, account, fromPath, toPath));

    /// <summary>Applies the range operations and writes of <paramref name="group"/>, in the order given, to <paramref name="batch"/>.</summary>
    /// <param name="group">The buffers to commit in one Paprika batch.</param>
    /// <param name="preBatch">A reader of the root <paramref name="batch"/> was begun on.</param>
    /// <param name="batch">The Paprika batch; its markers are left to the caller.</param>
    public static void ApplyTo(ReadOnlySpan<PaprikaWriteBuffer> group, IVisitableReadOnlyBatch preBatch, IBatch batch)
    {
        foreach (PaprikaWriteBuffer buffer in group) buffer.ApplyRanges(preBatch, batch, structural: true);
        foreach (PaprikaWriteBuffer buffer in group) buffer.ApplyRanges(preBatch, batch, structural: false);
        foreach (PaprikaWriteBuffer buffer in group) buffer.ApplyRecords(batch);
    }

    /// <summary>Returns the pooled chunks.</summary>
    public void Dispose()
    {
        foreach (ArrayPoolList<byte> chunk in _chunks) chunk.Dispose();
        _chunks.Clear();
    }

    private void AddRecord(RecordKind kind, in ValueHash256 account, ReadOnlySpan<byte> slot, ReadOnlySpan<byte> value)
    {
        if (value.Length > byte.MaxValue)
        {
            throw new ArgumentException($"A PaprikaFlat value is at most {byte.MaxValue} bytes; got {value.Length}.", nameof(value));
        }

        int length = 1 + PaprikaFlatKeys.AccountKeyLength + slot.Length + 1 + value.Length;
        if (_chunks.Count == 0 || _chunks[^1].Count + length > ChunkSize) _chunks.Add(new ArrayPoolList<byte>(ChunkSize));

        ArrayPoolList<byte> chunk = _chunks[^1];
        chunk.Add((byte)kind);
        chunk.AddRange(account.Bytes[..PaprikaFlatKeys.AccountKeyLength]);
        chunk.AddRange(slot);
        chunk.Add((byte)value.Length);
        chunk.AddRange(value);
    }

    private void ApplyRanges(IVisitableReadOnlyBatch preBatch, IBatch batch, bool structural)
    {
        foreach (RangeOperation range in _ranges) range.ApplyTo(preBatch, batch, structural);
    }

    private void ApplyRecords(IBatch batch)
    {
        // The padding after the account key stays zero.
        Span<byte> storageAccount = stackalloc byte[ValueHash256.MemorySize];
        foreach (ArrayPoolList<byte> chunk in _chunks)
        {
            ReadOnlySpan<byte> records = chunk.AsSpan();
            while (!records.IsEmpty)
            {
                RecordKind kind = (RecordKind)records[0];
                ReadOnlySpan<byte> account = records.Slice(1, PaprikaFlatKeys.AccountKeyLength);
                records = records[(1 + PaprikaFlatKeys.AccountKeyLength)..];

                ReadOnlySpan<byte> slot = kind == RecordKind.Slot ? records[..SlotLength] : default;
                records = records[slot.Length..];

                ReadOnlySpan<byte> value = records.Slice(1, records[0]);
                records = records[(1 + value.Length)..];

                if (kind == RecordKind.Account)
                {
                    batch.SetRaw(PaprikaFlatKeys.Account(account), value);
                }
                else
                {
                    account.CopyTo(storageAccount);
                    batch.SetRaw(PaprikaFlatKeys.Slot(storageAccount, slot), value);
                }
            }
        }
    }

    private enum RecordKind : byte
    {
        Account = 1,
        Slot = 2,
    }

    private enum RangeKind : byte
    {
        SelfDestruct,
        SelfDestructRange,
        DeleteAccountRange,
        DeleteStorageRange,
    }

    private readonly record struct RangeOperation(RangeKind Kind, ValueHash256 Account, ValueHash256 From, ValueHash256 To)
    {
        /// <summary>Applies its prefix deletes, which a group applies before every key-by-key delete, or its key-by-key deletes.</summary>
        /// <param name="structural">Whether to apply the prefix deletes.</param>
        public void ApplyTo(IVisitableReadOnlyBatch preBatch, IBatch batch, bool structural)
        {
            switch (Kind)
            {
                case RangeKind.SelfDestruct when structural:
                    PaprikaFlatRanges.SelfDestruct(preBatch, batch, Account);
                    break;
                case RangeKind.SelfDestructRange when structural:
                    PaprikaFlatRanges.SelfDestructRange(preBatch, batch, From, To);
                    break;
                case RangeKind.DeleteAccountRange:
                    PaprikaFlatRanges.DeleteAccountRange(preBatch, batch, From, To, structural);
                    break;
                case RangeKind.DeleteStorageRange:
                    PaprikaFlatRanges.DeleteStorageRange(preBatch, batch, Account, From, To, structural);
                    break;
            }
        }
    }
}
