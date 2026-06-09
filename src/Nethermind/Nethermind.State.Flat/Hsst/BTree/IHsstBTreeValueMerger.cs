// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Hsst.BTree;

/// <summary>
/// Per-emitted-key value merger for
/// <see cref="HsstBTreeMerger.NWayMerge{TWriter,TWriterReader,TWriterPin,TReader,TPin,TSource,TValueMerger}"/>.
/// <see cref="MergeValues"/> is invoked once per emitted key to write the merged value
/// across the matching sources.
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
internal interface IHsstBTreeValueMerger<TWriter, TReader, TPin, TSource, TFactory>
    where TPin : struct, IBufferPin, allows ref struct
    where TReader : IHsstByteReader<TPin>, allows ref struct
    where TSource : struct, IHsstMergeSource<TReader, TPin>
    where TFactory : struct, IHsstEnumeratorFactory<TReader, TPin>
{
    /// <summary>Fired once per emitted key to write the merged value. Emit the merged value
    /// bytes through <paramref name="writer"/> (the outer builder has already opened
    /// <see cref="HsstBTreeBuilder{TWriter,TReader,TPin}.BeginValueWrite"/> on the caller's
    /// behalf), inlining any per-element bookkeeping (e.g. bloom adds). A single matching
    /// source is the degenerate case of the same merge. Access matching sources via
    /// <see cref="NWayMergeCursor{TReader,TPin,TSource,TFactory}.MatchingSources"/>,
    /// <c>cursor.ValueAt(srcIdx)</c>, and <c>cursor.CreateReaderAt(srcIdx)</c>.</summary>
    void MergeValues(ref TWriter writer, scoped ReadOnlySpan<byte> key,
        scoped ref NWayMergeCursor<TReader, TPin, TSource, TFactory> cursor);
}
