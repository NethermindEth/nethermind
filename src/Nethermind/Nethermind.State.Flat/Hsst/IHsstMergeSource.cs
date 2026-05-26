// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// One participant in an N-way HSST merge driven by <see cref="NWayMergeCursor{TReader,TPin,TSource}"/>.
/// One instance per source: the source's pre-positioned enumerator plus the means to
/// materialise a fresh reader on demand (readers are typically ref structs, so they can't
/// be cached as fields and must be reconstructed each time the cursor advances).
/// </summary>
/// <remarks>
/// Implementations are usually small value-type structs the caller builds once per merge
/// (one per source) and passes via <c>Span&lt;TSource&gt;</c>. JIT monomorphises per source
/// type so <see cref="GetEnumerator"/> / <see cref="CreateReader"/> resolve to direct calls
/// in the cursor's hot loop.
/// </remarks>
internal interface IHsstMergeSource<TReader, TPin> : IDisposable
    where TPin : struct, IBufferPin, allows ref struct
    where TReader : IHsstByteReader<TPin>, allows ref struct
{
    /// <summary>The source's pre-positioned enumerator. Returned by value; iteration state
    /// lives on the heap behind the enumerator's struct envelope, so the copy still observes
    /// the underlying cursor.</summary>
    HsstEnumerator<TReader, TPin> GetEnumerator();

    /// <summary>Materialise a fresh reader scoped to this source. Called once per cursor
    /// advance and once per value pin during the merge.</summary>
    TReader CreateReader();

    // Dispose (inherited from IDisposable): release the source's enumerator and any other
    // per-source resources. Called by the merge driver once per source after the cursor
    // has finished consuming it.
}
