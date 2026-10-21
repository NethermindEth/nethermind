// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using Nethermind.Pbt.NodeBuffer;

namespace Nethermind.Pbt;

/// <summary>The node groups of one tree that <see cref="TrieUpdater"/> reads and writes: a group record buffer and the groups it does not hold.</summary>
public interface IPbtStore
{
    /// <summary>Finds a borrowed record, or misses when its pointer is absent or no longer available.</summary>
    bool TryLocate(ulong pointer, out PbtGroupRecord record);

    /// <summary>Reserves contiguous, zero-filled bytes for group records, registered with the store when the batch is disposed.</summary>
    /// <remarks>
    /// Any number of batches may be written concurrently, each started and disposed on any thread. The caller fills the batch
    /// with complete records before publishing their pointers to a parent. The store keeps the records alive through fold
    /// completion and publication. A removed group writes no record: it is simply no longer linked.
    /// </remarks>
    /// <param name="alignedTotalLength">The total length of the records, a multiple of <see cref="PbtGroupRecordCodec.RecordAlignment"/>.</param>
    IPbtGroupRecordWriteBatch StartWriteBatch(int alignedTotalLength);

    /// <summary>Loads a group <see cref="TryLocate"/> does not resolve, by its path.</summary>
    /// <remarks>The record stays readable through the fold. The path is borrowed only for this call.</remarks>
    /// <returns>False when the store has no group at <paramref name="path"/>.</returns>
    bool TryLoad(scoped in PbtTraversalPath path, out PbtGroupRecord record);
}

/// <summary>Pooled bytes whose records are handed to <paramref name="onRecord"/> when the batch is disposed, for stores that keep groups by path.</summary>
/// <remarks>
/// A record borrows the pooled memory only for the callback. The records link no pointer, so such a store serves the fold's
/// reads through <see cref="IPbtStore.TryLoad"/>.
/// </remarks>
public sealed class PbtRecordCallbackWriteBatch : IPbtGroupRecordWriteBatch
{
    private readonly Action<PbtGroupRecord> _onRecord;
    private readonly byte[] _bytes;
    private readonly int _length;

    public PbtRecordCallbackWriteBatch(Action<PbtGroupRecord> onRecord, int alignedTotalLength)
    {
        _onRecord = onRecord;
        _length = alignedTotalLength;
        _bytes = ArrayPool<byte>.Shared.Rent(alignedTotalLength);
        Bytes.Clear();
    }

    public ulong BasePointer => 0;

    public Span<byte> Bytes => _bytes.AsSpan(0, _length);

    public void Dispose()
    {
        try
        {
            for (int offset = 0; offset < _length; offset += PbtGroupRecordCodec.AlignedLength(PbtGroupRecordCodec.Length(_bytes.AsSpan(offset))))
                _onRecord(new PbtGroupRecord(_bytes.AsMemory(0, _length), offset));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(_bytes);
        }
    }
}
