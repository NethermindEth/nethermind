// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Persistence.PaprikaFlat;
using Paprika;
using Paprika.Store;

namespace Nethermind.State.Flat.Test.Persistence.PaprikaFlat;

/// <summary>An in-memory Paprika file that commits write buffers as one group, as the PaprikaFlat store does.</summary>
internal sealed class PaprikaFlatTestDb : IDisposable
{
    private readonly PagedDb _db = PagedDb.NativeMemoryDb(16 * 1024 * 1024, 2);

    /// <summary>A path whose bytes are zero but for the first, the twentieth (the last of the account key) and the last.</summary>
    public static ValueHash256 Path(byte first, byte twentieth = 0, byte last = 0)
    {
        ValueHash256 path = default;
        path.BytesAsSpan[0] = first;
        path.BytesAsSpan[PaprikaFlatKeys.AccountKeyLength - 1] = twentieth;
        path.BytesAsSpan[^1] = last;
        return path;
    }

    /// <summary>Applies <paramref name="group"/> to one batch over the root before it and, if any buffer changes the markers, writes every buffer's merged in order.</summary>
    public void Commit(params ReadOnlySpan<PaprikaWriteBuffer> group)
    {
        using IVisitableReadOnlyBatch preBatch = _db.BeginReadOnlyBatch();
        using IBatch batch = _db.BeginNextBatch();
        PaprikaWriteBuffer.ApplyTo(group, preBatch, batch);

        PaprikaFlatMetadata before = PaprikaFlatMetadata.Read(preBatch);
        PaprikaFlatMetadata metadata = before;
        foreach (PaprikaWriteBuffer buffer in group) metadata = metadata.Merge(buffer.Metadata.Changes);
        if (!ReferenceEquals(metadata, before)) metadata.WriteTo(batch);

        batch.Commit(CommitOptions.FlushDataAndRoot).AsTask().GetAwaiter().GetResult();
    }

    /// <summary>Commits the writes of <paramref name="write"/> alone.</summary>
    public void Write(Action<PaprikaFlatWriteBatch> write)
    {
        using PaprikaWriteBuffer buffer = new(StateId.Sync, StateId.Sync);
        write(new PaprikaFlatWriteBatch(buffer));
        Commit(buffer);
    }

    public IVisitableReadOnlyBatch BeginReadOnlyBatch() => _db.BeginReadOnlyBatch();

    public IBatch BeginNextBatch() => _db.BeginNextBatch();

    /// <summary>The pages the file uses.</summary>
    public uint NextFreePage => _db.NextFreePage;

    /// <summary>The accounts in <c>[<paramref name="start"/>, <paramref name="end"/>)</c>, as the account iterator yields them.</summary>
    public static List<(ValueHash256 Key, string Value)> Accounts(IVisitableReadOnlyBatch reader, in ValueHash256 start, in ValueHash256 end)
    {
        using IPersistence.IFlatIterator accounts = new PaprikaFlatReader(reader).CreateAccountIterator(start, end);
        return Collect(accounts);
    }

    /// <summary>The slots in <c>[<paramref name="start"/>, <paramref name="end"/>]</c> of <paramref name="account"/>, as the storage iterator yields them.</summary>
    public static List<(ValueHash256 Key, string Value)> Slots(IVisitableReadOnlyBatch reader, in ValueHash256 account, in ValueHash256 start, in ValueHash256 end)
    {
        using IPersistence.IFlatIterator slots = new PaprikaFlatReader(reader).CreateStorageIterator(account, start, end);
        return Collect(slots);
    }

    /// <summary>Every slot of <paramref name="account"/>.</summary>
    public static List<(ValueHash256 Key, string Value)> Slots(IVisitableReadOnlyBatch reader, in ValueHash256 account) =>
        Slots(reader, account, ValueKeccak.Zero, ValueKeccak.MaxValue);

    public void Dispose() => _db.Dispose();

    /// <summary>The entries <paramref name="iterator"/> yields, with their values in hex.</summary>
    public static List<(ValueHash256 Key, string Value)> Collect(IPersistence.IFlatIterator iterator)
    {
        List<(ValueHash256, string)> entries = [];
        while (iterator.MoveNext()) entries.Add((iterator.CurrentKey, iterator.CurrentValue.ToArray().ToHexString(true)));
        return entries;
    }
}
