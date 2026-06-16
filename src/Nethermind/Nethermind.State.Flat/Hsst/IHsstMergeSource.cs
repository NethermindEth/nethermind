// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// One participant in an N-way HSST merge driven by
/// <see cref="NWayMergeCursor{TReader,TPin,TSource,TFactory}"/>. A source carries the
/// minimal "what to merge" pair: a reader factory (since readers are typically ref
/// structs and can't be cached as fields) plus the <see cref="Bound"/> scope this slot
/// is positioned over. The cursor constructs the per-slot
/// <see cref="HsstEnumerator{TReader,TPin}"/> in its ctor via the
/// <c>TFactory</c> generic parameter.
/// </summary>
/// <remarks>
/// Implementations are usually small value-type structs the caller builds once per merge
/// (one per source) and passes via <c>Span&lt;TSource&gt;</c>. JIT monomorphises per source
/// type so <see cref="CreateReader"/> / <see cref="Bound"/> resolve to direct calls in the
/// cursor's hot loop.
/// </remarks>
internal interface IHsstMergeSource<TReader, TPin> : IHsstReaderSource<TReader, TPin>
    where TPin : struct, IBufferPin, allows ref struct
    where TReader : IHsstByteReader<TPin>, allows ref struct
{
    /// <summary>Passed to <see cref="IHsstEnumeratorFactory{TReader,TPin}"/> at cursor
    /// construction time to position the per-slot enumerator.</summary>
    Bound Bound { get; }
}
