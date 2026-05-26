// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Hsst.BTree;

/// <summary>
/// N-way merge driver that emits a single <see cref="IndexType.BTree"/> HSST from N
/// pre-positioned source enumerators. Drives a <see cref="NWayMergeCursor{TReader,TPin,TSource}"/>
/// over the sources; on every cursor advance, fast-paths the matchCount==1 case by
/// copying the source value verbatim via
/// <see cref="HsstBTreeBuilder{TWriter,TReader,TPin}.TryAddAligned"/>, otherwise opens
/// <see cref="HsstBTreeBuilder{TWriter,TReader,TPin}.BeginValueWrite"/> and delegates to
/// <typeparamref name="TValueMerger"/>.<see cref="IHsstBTreeValueMerger{TWriter,TReader,TPin,TSource}.MergeValues"/>
/// for conflict resolution.
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
    /// <param name="valueMerger">Per-key callback bundle. <c>OnKey</c> fires once per emitted
    /// key (path-independent bookkeeping), <c>OnFastCopy</c> on a successful verbatim copy
    /// of a single source's value, <c>MergeValues</c> on conflict / oversized single source.</param>
    /// <param name="options">Forwarded to the underlying builder.</param>
    /// <param name="expectedKeyCount">Forwarded to the underlying builder (sizing hint).</param>
    /// <param name="keyFirst">Forwarded to the underlying builder (entry layout selector).</param>
    internal static void NWayMerge<TWriter, TWriterReader, TWriterPin, TReader, TPin, TSource, TValueMerger>(
        ref TWriter writer,
        int keyLength,
        scoped ref NWayMergeCursor<TReader, TPin, TSource> cursor,
        scoped ref TValueMerger valueMerger,
        HsstBTreeOptions? options = null,
        int expectedKeyCount = 16,
        bool keyFirst = false)
        where TWriter : IByteBufferWriterWithReader<TWriterReader, TWriterPin>
        where TWriterPin : struct, IBufferPin, allows ref struct
        where TWriterReader : IHsstByteReader<TWriterPin>, allows ref struct
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
        where TSource : struct, IHsstMergeSource<TReader, TPin>
        where TValueMerger : struct, IHsstBTreeValueMerger<TWriter, TReader, TPin, TSource>, allows ref struct
    {
        // builder is referenced indirectly across MergeValues via BeginValueWrite; the
        // compiler refuses `ref` to a `using`-declared local, so manage disposal manually
        // via try/finally (same pattern as PersistedSnapshotMerger's BTree call sites).
        HsstBTreeBuilder<TWriter, TWriterReader, TWriterPin> builder =
            new(ref writer, keyLength, options, expectedKeyCount, keyFirst);
        try
        {
            while (cursor.MoveNext())
            {
                bool emittedFast = false;
                if (cursor.MatchCount == 1)
                {
                    Bound vb = cursor.MinValue;
                    // Fast-fail short-circuit: NoOpPin.PinBuffer casts size to int and would
                    // throw on a >2 GiB blob, so skip the pin attempt for obviously
                    // disqualified sizes. TryAddAligned still does its own precise entry-
                    // size check internally for the in-range cases.
                    if (vb.Length <= PageLayout.PageSize)
                    {
                        TReader r = cursor.CreateMinReader();
                        using TPin p = r.PinBuffer(vb.Offset, vb.Length);
                        emittedFast = builder.TryAddAligned(cursor.MinKey, p.Buffer);
                    }
                }

                if (emittedFast)
                {
                    valueMerger.OnFastCopy(cursor.MinKey, ref cursor);
                }
                else
                {
                    ref TWriter inner = ref builder.BeginValueWrite();
                    valueMerger.MergeValues(ref inner, cursor.MinKey, ref cursor);
                    builder.FinishValueWrite(cursor.MinKey);
                }
                valueMerger.OnKey(cursor.MinKey);
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
