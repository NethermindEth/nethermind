// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using Nethermind.Core.Buffers;
using Nethermind.Core.Memory;
using Nethermind.Pbt.NodeBuffer;

namespace Nethermind.Pbt;

/// <summary>Destination of complete node-group replacements; stores accept them only through an <see cref="IPbtConcurrentWriter"/>.</summary>
public interface IPbtNodeGroupSink
{
    /// <summary>Reserves contiguous, zero-filled bytes for group records, registered with the buffer when the batch is disposed.</summary>
    /// <remarks>
    /// The caller fills the batch with complete records before publishing their pointers to a parent. The store keeps the
    /// records alive through fold completion and publication. A removed group writes no record: it is simply no longer linked.
    /// </remarks>
    /// <param name="alignedTotalLength">The total length of the records, a multiple of <see cref="PbtGroupRecordCodec.RecordAlignment"/>.</param>
    IPbtGroupRecordWriteBatch StartWriteBatch(int alignedTotalLength);
}

/// <summary>Backing store used by <see cref="TrieUpdater"/> for canonical complete-key mutations.</summary>
public interface IPbtStore
{
    /// <summary>The pointer of the input root's group, or 0 when it is not in the buffer.</summary>
    ulong RootGroupPointer => 0;

    /// <summary>Borrows a direct record for this path, or returns false to use the exact store lookup.</summary>
    /// <remarks>The store protects the record's lifetime through worker completion and publication.</remarks>
    bool TryGetNodeGroupRecord(ulong pointer, scoped in PbtTraversalPath groupKey, out PbtGroupRecord record)
    {
        record = default;
        return false;
    }

    /// <summary>Marks a successful root fold, allowing stores to publish its handed-off writes on disposal.</summary>
    /// <remarks>A fold without changes is not completed, as it writes nothing; a completed fold that wrote no root group emptied the tree.</remarks>
    void CompleteFold() { }

    /// <summary>Gets a caller-owned lease for the complete group identified by <paramref name="groupKey"/>.</summary>
    /// <remarks>
    /// A non-null result transfers exactly one reference to the caller, which must invoke
    /// <see cref="IDisposable.Dispose"/> exactly once. Consumers must treat the payload as immutable.
    /// The reference keeps the payload valid until released, independently of the store. A missing group
    /// returns <see langword="null"/>. Implementations validate group boundary keys even on misses.
    /// The path is borrowed only for this call; retained identities must be immutable snapshots,
    /// never aliases of the cursor buffer.
    /// </remarks>
    RefCountingMemory? GetNodeGroup(scoped in PbtTraversalPath groupKey);

    /// <summary>Creates a writer for one worker; every group replacement reaches this store through one.</summary>
    /// <remarks>
    /// A writer is not thread-safe, but any number of them may be used concurrently, and each may be created and
    /// disposed on any thread. When a disposed writer's groups become visible through this store is up to the store.
    /// </remarks>
    IPbtConcurrentWriter CreateWriter();
}

/// <summary>A single-worker writer created by <see cref="IPbtStore.CreateWriter"/>; disposing it hands its writes to the store.</summary>
public interface IPbtConcurrentWriter : IPbtNodeGroupSink, IDisposable;

/// <summary>Hands every record to <paramref name="onRecord"/> as soon as its batch is disposed, for stores that keep groups by path.</summary>
/// <remarks>
/// Each batch is pooled memory that the record borrows only for the callback. Its records link no pointer, so such a store
/// serves the fold's reads through <see cref="IPbtStore.GetNodeGroup"/>.
/// </remarks>
public sealed class PbtRecordCallbackWriter(Action<PbtGroupRecord> onRecord) : IPbtConcurrentWriter
{
    /// <inheritdoc/>
    public IPbtGroupRecordWriteBatch StartWriteBatch(int alignedTotalLength) => new WriteBatch(onRecord, alignedTotalLength);

    public void Dispose() { }

    private sealed class WriteBatch : IPbtGroupRecordWriteBatch
    {
        private readonly Action<PbtGroupRecord> _onRecord;
        private readonly byte[] _bytes;
        private readonly int _length;

        public WriteBatch(Action<PbtGroupRecord> onRecord, int length)
        {
            _onRecord = onRecord;
            _length = length;
            _bytes = ArrayPool<byte>.Shared.Rent(length);
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
}
