// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Hsst.BTree;

/// <summary>
/// Per-emitted-key value merger for
/// <see cref="HsstBTreeMerger.NWayMerge{TWriter,TReader,TPin,TSource,TFactory,TValueMerger}"/>.
/// <see cref="MergeValues"/> is invoked once per emitted key to write the merged value
/// across the matching sources.
/// </summary>
/// <remarks>
/// A generic struct constraint (<c>TValueMerger : struct, IHsstBTreeValueMerger&lt;...&gt;</c>)
/// lets the JIT monomorphise per callback type, so every hook resolves to a direct, non-virtual
/// call. Unlike <see cref="IHsstMergeKeyCallback"/> (key-only), <see cref="MergeValues"/> needs
/// builder + cursor access because BTree collisions resolve by re-emitting a per-key inner
/// structure rather than picking a winner.
/// <para><typeparamref name="TReader"/>/<typeparamref name="TPin"/> describe the cursor (source)
/// side; the destination <typeparamref name="TWriter"/> is the builder's writer. The cursor is
/// passed <c>in</c> (read-only) so the builder, a ref struct, can be passed by <c>ref</c> without
/// tripping ref-safety.</para>
/// </remarks>
internal interface IHsstBTreeValueMerger<TWriter, TReader, TPin, TSource, TFactory>
    where TWriter : IByteBufferWriter
    where TPin : struct, IBufferPin, allows ref struct
    where TReader : IHsstByteReader<TPin>, allows ref struct
    where TSource : struct, IHsstMergeSource<TReader, TPin>
    where TFactory : struct, IHsstEnumeratorFactory<TReader, TPin>
{
    /// <summary>Fired once per emitted key to write the merged value. The handler opens its own
    /// value write on <paramref name="builder"/>: streaming mergers call
    /// <see cref="HsstBTreeBuilder{TWriter}.BeginValueWrite"/> /
    /// <see cref="HsstBTreeBuilder{TWriter}.FinishValueWrite"/>; key-first mergers stage the value
    /// and call <see cref="HsstBTreeBuilder{TWriter}.Add"/>. Inline any per-element bookkeeping
    /// (e.g. bloom adds) here. A single matching source is the degenerate case of the same merge.
    /// Access matching sources via
    /// <see cref="NWayMergeCursor{TReader,TPin,TSource,TFactory}.MatchingSources"/>
    /// and <c>cursor.ValueAt(srcIdx)</c>.</summary>
    void MergeValues(scoped ref HsstBTreeBuilder<TWriter> builder, scoped ReadOnlySpan<byte> key,
        scoped in NWayMergeCursor<TReader, TPin, TSource, TFactory> cursor);
}
