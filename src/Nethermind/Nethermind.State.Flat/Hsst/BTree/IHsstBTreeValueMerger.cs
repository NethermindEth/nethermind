// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Hsst.BTree;

/// <summary>
/// Per-emitted-key callback bundle for
/// <see cref="HsstBTreeMerger.NWayMerge{TWriter,TWriterReader,TWriterPin,TReader,TPin,TSource,TValueMerger}"/>.
/// Covers the three distinct lifecycle points of a BTree key emit: the path-independent
/// post-write hook (<see cref="OnKey"/>), the verbatim-copy fast-path hook
/// (<see cref="OnFastCopy"/>), and the actual multi-source value merge
/// (<see cref="MergeValues"/>). Callers supply explicit empty bodies for the hooks they
/// don't need.
/// </summary>
/// <remarks>
/// Implemented as a generic struct constraint
/// (<c>TValueMerger : struct, IHsstBTreeValueMerger&lt;...&gt;</c>) so the JIT monomorphises
/// the merger per callback type — every hook call resolves to a direct invocation, no
/// virtual dispatch. Unlike <see cref="IHsstPackedArrayMergeCallback"/> (key-only),
/// <see cref="MergeValues"/> needs writer + cursor access because BTree collisions resolve
/// by re-emitting a per-key inner structure rather than picking a winner.
/// <para><typeparamref name="TReader"/>/<typeparamref name="TPin"/> describe the CURSOR
/// (source) side; the writer's reader/pin are independent and are wired by the implementer
/// directly (commonly via the implementer's own generic parameters that don't appear here).
/// <typeparamref name="TWriter"/> is therefore unconstrained at the interface level.</para>
/// </remarks>
internal interface IHsstBTreeValueMerger<TWriter, TReader, TPin, TSource>
    where TPin : struct, IBufferPin, allows ref struct
    where TReader : IHsstByteReader<TPin>, allows ref struct
    where TSource : struct, IHsstMergeSource<TReader, TPin>
{
    /// <summary>Fired once per emitted key (single-source verbatim copy and multi-source
    /// rebuild alike), AFTER the value has been written into the outer builder. Use for
    /// path-independent outer-key bookkeeping (e.g. <c>bloom.Add(addrKey)</c>). Supply an
    /// empty body when not needed.</summary>
    void OnKey(scoped ReadOnlySpan<byte> key);

    /// <summary>Fired when matchCount==1 AND the source value was copied verbatim through
    /// <see cref="HsstBTreeBuilder{TWriter,TReader,TPin}.TryAddAligned"/>. The destination
    /// has no inner structure to walk, so this hook walks the SOURCE bytes for per-element
    /// bookkeeping (e.g. iterating the source's per-address slot HSST to bloom-add each
    /// slot key). Read source bytes via <c>cursor.MinValue</c> + <c>cursor.CreateMinReader()</c>.
    /// Supply an empty body when not needed.</summary>
    void OnFastCopy(scoped ReadOnlySpan<byte> key,
        scoped ref NWayMergeCursor<TReader, TPin, TSource> cursor);

    /// <summary>Fired when the value must be merged: matchCount &gt; 1, OR matchCount==1
    /// with a verbatim copy that didn't fit page-aligned. Emit the merged value bytes
    /// through <paramref name="writer"/> (the outer builder has already opened
    /// <see cref="HsstBTreeBuilder{TWriter,TReader,TPin}.BeginValueWrite"/> on the caller's
    /// behalf). Inline any per-element bookkeeping that <see cref="OnFastCopy"/> would have
    /// done on a verbatim copy. Access matching sources via
    /// <see cref="NWayMergeCursor{TReader,TPin,TSource}.MatchingSources"/>,
    /// <c>cursor.ValueAt(srcIdx)</c>, and <c>cursor.CreateReaderAt(srcIdx)</c>.</summary>
    void MergeValues(ref TWriter writer, scoped ReadOnlySpan<byte> key,
        scoped ref NWayMergeCursor<TReader, TPin, TSource> cursor);
}
