// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Hsst.BTree;

/// <summary>
/// N-way merge driver that emits a single <see cref="IndexType.BTree"/> (or
/// <see cref="IndexType.BTreeKeyFirst"/> when <c>keyFirst</c> is set) HSST from N
/// pre-positioned source enumerators. Drives a <see cref="NWayMergeCursor{TReader,TPin,TSource}"/>
/// over the sources; on every cursor advance it hands the builder to
/// <typeparamref name="TValueMerger"/>.<see cref="IHsstBTreeValueMerger{TWriter,TReader,TPin,TSource}.MergeValues"/>,
/// which opens its own value write — the framework never opens one on the merger's behalf.
/// A single matching source is the degenerate case of the same merge.
/// </summary>
/// <remarks>
/// The destination writer (<typeparamref name="TWriter"/>) and the cursor's reader/pin/source
/// trio (<typeparamref name="TReader"/>, <typeparamref name="TPin"/>,
/// <typeparamref name="TSource"/>) are independent — the cursor reads from the merge sources
/// while the builder only writes, so they can have entirely different storage backings. Generic
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
    /// <param name="expectedKeyCount">Forwarded to the underlying builder (sizing hint).</param>
    /// <param name="keyFirst">Forwarded to the underlying builder (entry layout selector).</param>
    internal static void NWayMerge<TWriter, TReader, TPin, TSource, TFactory, TValueMerger>(
        ref TWriter writer,
        int keyLength,
        scoped ref NWayMergeCursor<TReader, TPin, TSource, TFactory> cursor,
        TValueMerger valueMerger,
        int expectedKeyCount = 16,
        bool keyFirst = false)
        where TWriter : IByteBufferWriter
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
        where TSource : struct, IHsstMergeSource<TReader, TPin>
        where TFactory : struct, IHsstEnumeratorFactory<TReader, TPin>
        where TValueMerger : struct, IHsstBTreeValueMerger<TWriter, TReader, TPin, TSource, TFactory>
    {
        using HsstBTreeBuilderBuffers.Container buffers = new(expectedKeyCount);
        NWayMerge<TWriter, TReader, TPin, TSource, TFactory, TValueMerger>(
            ref writer, keyLength, ref cursor, valueMerger,
            ref buffers.Buffers, expectedKeyCount, keyFirst);
    }

    /// <summary>
    /// External-buffer overload: drives the same merge but uses the caller's
    /// <see cref="HsstBTreeBuilderBuffers"/> instead of allocating its own container. Used
    /// when the buffers are reused across many merges in a single outer pass — e.g. one
    /// per-address slot-prefix BTree reuses the same container for every address in a
    /// per-address column merge.
    /// </summary>
    internal static void NWayMerge<TWriter, TReader, TPin, TSource, TFactory, TValueMerger>(
        ref TWriter writer,
        int keyLength,
        scoped ref NWayMergeCursor<TReader, TPin, TSource, TFactory> cursor,
        TValueMerger valueMerger,
        scoped ref HsstBTreeBuilderBuffers externalBuffers,
        int expectedKeyCount = 16,
        bool keyFirst = false)
        where TWriter : IByteBufferWriter
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
        where TSource : struct, IHsstMergeSource<TReader, TPin>
        where TFactory : struct, IHsstEnumeratorFactory<TReader, TPin>
        where TValueMerger : struct, IHsstBTreeValueMerger<TWriter, TReader, TPin, TSource, TFactory>
    {
        // builder is passed by ref into MergeValues, which opens its own value write; the
        // compiler refuses `ref` to a `using`-declared local, so manage disposal manually
        // via try/finally (same pattern as PersistedSnapshotMerger's BTree call sites).
        HsstBTreeBuilder<TWriter> builder =
            new(ref writer, ref externalBuffers, keyLength, expectedKeyCount, keyFirst);
        try
        {
            while (cursor.MoveNext())
            {
                valueMerger.MergeValues(ref builder, cursor.MinKey, in cursor);
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
