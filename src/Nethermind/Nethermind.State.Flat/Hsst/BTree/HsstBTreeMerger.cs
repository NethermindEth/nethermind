// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Hsst.BTree;

/// <summary>
/// N-way merge driver that emits a single <see cref="IndexType.BTree"/> HSST from N
/// pre-positioned source enumerators. Drives a <see cref="NWayMergeCursor{TReader,TPin,TSource}"/>
/// over the sources; on every cursor advance opens
/// <see cref="HsstBTreeBuilder{TWriter,TReader,TPin}.BeginValueWrite"/> and delegates to
/// <typeparamref name="TValueMerger"/>.<see cref="IHsstBTreeValueMerger{TWriter,TReader,TPin,TSource}.MergeValues"/>
/// for conflict resolution (a single matching source is the degenerate case of the same merge).
/// </summary>
/// <remarks>
/// Writer-side and cursor-side reader/pin types are independent — the cursor reads from
/// the merge sources, the builder reads back from the destination writer during the index
/// build; the two can have entirely different storage backings. Hence the two separate
/// generic trios: (<typeparamref name="TWriter"/>, <typeparamref name="TWriterReader"/>,
/// <typeparamref name="TWriterPin"/>) for the builder and (<typeparamref name="TReader"/>,
/// <typeparamref name="TPin"/>, <typeparamref name="TSource"/>) for the cursor. Generic
/// over <typeparamref name="TValueMerger"/> (struct constraint with
/// <c>allows ref struct</c>) so the JIT monomorphises each merger call site and resolves
/// every hook to a direct invocation — no virtual dispatch, no allocation.
/// </remarks>
internal static class HsstBTreeMerger
{
    /// <param name="writer">Destination writer; receives one BTree HSST.</param>
    /// <param name="keyLength">Logical key length in bytes (the cursor's
    /// <see cref="NWayMergeCursor{TReader,TPin,TSource}.KeyLen"/> must match).</param>
    /// <param name="cursor">Caller-constructed merge cursor over N pre-positioned sources.
    /// The merger drives it to exhaustion.</param>
    /// <param name="valueMerger">Per-key callback bundle. <c>MergeValues</c> emits the merged
    /// value for each key, resolving conflicts across the matching sources.</param>
    /// <param name="options">Forwarded to the underlying builder.</param>
    /// <param name="expectedKeyCount">Forwarded to the underlying builder (sizing hint).</param>
    /// <param name="keyFirst">Forwarded to the underlying builder (entry layout selector).</param>
    internal static void NWayMerge<TWriter, TWriterReader, TWriterPin, TReader, TPin, TSource, TFactory, TValueMerger>(
        ref TWriter writer,
        int keyLength,
        scoped ref NWayMergeCursor<TReader, TPin, TSource, TFactory> cursor,
        TValueMerger valueMerger,
        HsstBTreeOptions? options = null,
        int expectedKeyCount = 16,
        bool keyFirst = false)
        where TWriter : IByteBufferWriterWithReader<TWriterReader, TWriterPin>
        where TWriterPin : struct, IBufferPin, allows ref struct
        where TWriterReader : IHsstByteReader<TWriterPin>, allows ref struct
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
        where TSource : struct, IHsstMergeSource<TReader, TPin>
        where TFactory : struct, IHsstEnumeratorFactory<TReader, TPin>
        where TValueMerger : struct, IHsstBTreeValueMerger<TWriter, TReader, TPin, TSource, TFactory>
    {
        using HsstBTreeBuilderBuffersContainer buffers = new(expectedKeyCount);
        NWayMerge<TWriter, TWriterReader, TWriterPin, TReader, TPin, TSource, TFactory, TValueMerger>(
            ref writer, keyLength, ref cursor, valueMerger,
            ref buffers.Buffers, options, expectedKeyCount, keyFirst);
    }

    /// <summary>
    /// External-buffer overload: drives the same merge but uses the caller's
    /// <see cref="HsstBTreeBuilderBuffers"/> instead of allocating its own container. Used
    /// when the buffers are reused across many merges in a single outer pass — e.g. one
    /// per-address slot-prefix BTree reuses the same container for every address in a
    /// per-address column merge.
    /// </summary>
    internal static void NWayMerge<TWriter, TWriterReader, TWriterPin, TReader, TPin, TSource, TFactory, TValueMerger>(
        ref TWriter writer,
        int keyLength,
        scoped ref NWayMergeCursor<TReader, TPin, TSource, TFactory> cursor,
        TValueMerger valueMerger,
        scoped ref HsstBTreeBuilderBuffers externalBuffers,
        HsstBTreeOptions? options = null,
        int expectedKeyCount = 16,
        bool keyFirst = false)
        where TWriter : IByteBufferWriterWithReader<TWriterReader, TWriterPin>
        where TWriterPin : struct, IBufferPin, allows ref struct
        where TWriterReader : IHsstByteReader<TWriterPin>, allows ref struct
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
        where TSource : struct, IHsstMergeSource<TReader, TPin>
        where TFactory : struct, IHsstEnumeratorFactory<TReader, TPin>
        where TValueMerger : struct, IHsstBTreeValueMerger<TWriter, TReader, TPin, TSource, TFactory>
    {
        // builder is referenced indirectly across MergeValues via BeginValueWrite; the
        // compiler refuses `ref` to a `using`-declared local, so manage disposal manually
        // via try/finally (same pattern as PersistedSnapshotMerger's BTree call sites).
        HsstBTreeBuilder<TWriter, TWriterReader, TWriterPin> builder =
            new(ref writer, ref externalBuffers, keyLength, options, expectedKeyCount, keyFirst);
        try
        {
            while (cursor.MoveNext())
            {
                ref TWriter inner = ref builder.BeginValueWrite();
                long valueStart = inner.Written;
                valueMerger.MergeValues(ref inner, cursor.MinKey, ref cursor);
                builder.FinishValueWrite(cursor.MinKey, inner.Written - valueStart);
                cursor.AdvanceMatching();
            }
            builder.Build();
        }
        finally
        {
            builder.Dispose();
        }
    }

    /// <summary>
    /// Key-first variant of <see cref="NWayMerge{TWriter,TWriterReader,TWriterPin,TReader,TPin,TSource,TValueMerger}(ref TWriter,int,ref NWayMergeCursor{TReader,TPin,TSource},TValueMerger,ref HsstBTreeBuilderBuffers,HsstBTreeOptions?,int,bool)"/>:
    /// drives an <see cref="IndexType.BTreeKeyFirst"/> outer build, where the BTree
    /// builder requires the value's full length up front. Stages each emitted entry's
    /// value through an internal <see cref="PooledByteBufferWriter"/> (the value-merger
    /// writes there during <see cref="IHsstBTreeValueMerger{TWriter,TReader,TPin,TSource}.MergeValues"/>)
    /// and feeds the staged span into <c>builder.Add(key, span)</c>. The value-merger's
    /// writer type is therefore fixed to <see cref="PooledByteBufferWriter.Writer"/>,
    /// independent of the outer builder's writer type.
    /// </summary>
    internal static void NWayMergeKeyFirst<TBuilderWriter, TBuilderReader, TBuilderPin, TReader, TPin, TSource, TFactory, TValueMerger>(
        ref TBuilderWriter writer,
        int keyLength,
        scoped ref NWayMergeCursor<TReader, TPin, TSource, TFactory> cursor,
        TValueMerger valueMerger,
        scoped ref HsstBTreeBuilderBuffers externalBuffers,
        HsstBTreeOptions? options = null,
        int expectedKeyCount = 16)
        where TBuilderWriter : IByteBufferWriterWithReader<TBuilderReader, TBuilderPin>
        where TBuilderPin : struct, IBufferPin, allows ref struct
        where TBuilderReader : IHsstByteReader<TBuilderPin>, allows ref struct
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
        where TSource : struct, IHsstMergeSource<TReader, TPin>
        where TFactory : struct, IHsstEnumeratorFactory<TReader, TPin>
        where TValueMerger : struct, IHsstBTreeValueMerger<PooledByteBufferWriter.Writer, TReader, TPin, TSource, TFactory>
    {
        using PooledByteBufferWriter staging = new(4096);
        HsstBTreeBuilder<TBuilderWriter, TBuilderReader, TBuilderPin> builder =
            new(ref writer, ref externalBuffers, keyLength, options, expectedKeyCount, keyFirst: true);
        try
        {
            while (cursor.MoveNext())
            {
                staging.Reset();
                ref PooledByteBufferWriter.Writer stagingWriter = ref staging.GetWriter();
                valueMerger.MergeValues(ref stagingWriter, cursor.MinKey, ref cursor);
                builder.Add(cursor.MinKey, staging.WrittenSpan);
                cursor.AdvanceMatching();
            }
            builder.Build();
        }
        finally
        {
            builder.Dispose();
        }
    }

    /// <summary>
    /// Partitioned key-first variant of <see cref="NWayMergeKeyFirst{TBuilderWriter,TBuilderReader,TBuilderPin,TReader,TPin,TSource,TFactory,TValueMerger}"/>:
    /// emits a partitioned key-first (<see cref="IndexType.BTreeKeyFirst"/>) outer build via
    /// <see cref="HsstPartitionedBTreeBuilder{TWriter,TReader,TPin}"/> instead of a plain
    /// key-first B-tree, so compacted slot-prefix HSSTs keep the partition hashtables. Same
    /// merge loop and value-merger contract as the non-partitioned variant: every emitted key
    /// is staged through a <see cref="PooledByteBufferWriter"/> via
    /// <see cref="IHsstBTreeValueMerger{TWriter,TReader,TPin,TSource,TFactory}.MergeValues"/>
    /// (a single matching source is the degenerate case of the same merge) and fed into
    /// <c>builder.Add(key, span)</c>.
    /// </summary>
    internal static void NWayMergeKeyFirstPartitioned<TBuilderWriter, TBuilderReader, TBuilderPin, TReader, TPin, TSource, TFactory, TValueMerger>(
        ref TBuilderWriter writer,
        int keyLength,
        scoped ref NWayMergeCursor<TReader, TPin, TSource, TFactory> cursor,
        TValueMerger valueMerger,
        scoped ref HsstPartitionedBTreeBuilderBuffers externalBuffers,
        HsstBTreeOptions? options = null)
        where TBuilderWriter : IByteBufferWriterWithReader<TBuilderReader, TBuilderPin>
        where TBuilderPin : struct, IBufferPin, allows ref struct
        where TBuilderReader : IHsstByteReader<TBuilderPin>, allows ref struct
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
        where TSource : struct, IHsstMergeSource<TReader, TPin>
        where TFactory : struct, IHsstEnumeratorFactory<TReader, TPin>
        where TValueMerger : struct, IHsstBTreeValueMerger<PooledByteBufferWriter.Writer, TReader, TPin, TSource, TFactory>
    {
        using PooledByteBufferWriter staging = new(4096);
        HsstPartitionedBTreeBuilder<TBuilderWriter, TBuilderReader, TBuilderPin> builder =
            new(ref writer, ref externalBuffers, keyLength, options);
        try
        {
            while (cursor.MoveNext())
            {
                staging.Reset();
                ref PooledByteBufferWriter.Writer stagingWriter = ref staging.GetWriter();
                valueMerger.MergeValues(ref stagingWriter, cursor.MinKey, ref cursor);
                builder.Add(cursor.MinKey, staging.WrittenSpan);
                cursor.AdvanceMatching();
            }
            builder.Build();
        }
        finally
        {
            builder.Dispose();
        }
    }

    /// <summary>
    /// Partitioned variant of the streaming key-after-value
    /// <see cref="NWayMerge{TWriter,TWriterReader,TWriterPin,TReader,TPin,TSource,TFactory,TValueMerger}(ref TWriter,int,ref NWayMergeCursor{TReader,TPin,TSource,TFactory},TValueMerger,ref HsstBTreeBuilderBuffers,HsstBTreeOptions?,int,bool)"/>:
    /// emits a partitioned key-after-value (<see cref="IndexType.BTree"/>) outer build via
    /// <see cref="HsstPartitionedBTreeBuilder{TWriter,TReader,TPin}"/> so the compacted
    /// per-address column keeps its per-partition hashtables. Same value-merger contract and
    /// streaming model as the non-partitioned overload — every emitted key streams its merged
    /// value straight into the partitioned builder's
    /// <see cref="HsstPartitionedBTreeBuilder{TWriter,TReader,TPin}.BeginValueWrite"/> region via
    /// <see cref="IHsstBTreeValueMerger{TWriter,TReader,TPin,TSource,TFactory}.MergeValues"/>
    /// (a single matching source is the degenerate case of the same merge).
    /// </summary>
    internal static void NWayMergePartitioned<TWriter, TWriterReader, TWriterPin, TReader, TPin, TSource, TFactory, TValueMerger>(
        ref TWriter writer,
        int keyLength,
        scoped ref NWayMergeCursor<TReader, TPin, TSource, TFactory> cursor,
        TValueMerger valueMerger,
        scoped ref HsstPartitionedBTreeBuilderBuffers externalBuffers,
        HsstBTreeOptions? options = null)
        where TWriter : IByteBufferWriterWithReader<TWriterReader, TWriterPin>
        where TWriterPin : struct, IBufferPin, allows ref struct
        where TWriterReader : IHsstByteReader<TWriterPin>, allows ref struct
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
        where TSource : struct, IHsstMergeSource<TReader, TPin>
        where TFactory : struct, IHsstEnumeratorFactory<TReader, TPin>
        where TValueMerger : struct, IHsstBTreeValueMerger<TWriter, TReader, TPin, TSource, TFactory>
    {
        HsstPartitionedBTreeBuilder<TWriter, TWriterReader, TWriterPin> builder =
            new(ref writer, ref externalBuffers, keyLength, options, keyFirst: false);
        try
        {
            while (cursor.MoveNext())
            {
                ref TWriter inner = ref builder.BeginValueWrite();
                long valueStart = inner.Written;
                valueMerger.MergeValues(ref inner, cursor.MinKey, ref cursor);
                builder.FinishValueWrite(cursor.MinKey, inner.Written - valueStart);
                cursor.AdvanceMatching();
            }
            builder.Build();
        }
        finally
        {
            builder.Dispose();
        }
    }
}
