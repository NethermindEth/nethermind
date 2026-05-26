// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Per-emitted-key hook invoked by
/// <see cref="PackedArray.HsstPackedArrayMerger.NWayMerge{TWriter,TReader,TPin,TSource,TCallback}"/>
/// once per output key, after the merger has written that key+value into the destination
/// <c>HsstPackedArrayBuilder</c>. Used by consumers that maintain side-state per key (e.g. a
/// bloom filter) so they don't have to re-iterate the merger output.
/// </summary>
/// <remarks>
/// Implemented as a generic struct constraint (<c>TCallback : struct, IHsstPackedArrayMergeCallback</c>)
/// so the JIT monomorphises the merger per callback type — the <c>OnKey</c> call resolves to a
/// direct invocation, no virtual dispatch. <see cref="NoOpHsstPackedArrayMergeCallback"/> is
/// available for callers that don't need a hook.
/// </remarks>
internal interface IHsstPackedArrayMergeCallback
{
    void OnKey(scoped ReadOnlySpan<byte> key);
}

/// <summary>No-op <see cref="IHsstPackedArrayMergeCallback"/> for callers that don't need
/// the per-key hook.</summary>
internal readonly struct NoOpHsstPackedArrayMergeCallback : IHsstPackedArrayMergeCallback
{
    public void OnKey(scoped ReadOnlySpan<byte> key) { }
}
