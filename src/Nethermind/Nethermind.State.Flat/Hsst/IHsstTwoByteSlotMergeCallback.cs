// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Per-emitted-key hook invoked by
/// <see cref="TwoByteSlot.HsstTwoByteSlotMerger.NWayMerge{TWriter,TReader,TPin,TSource,TCallback}"/>
/// once per output key, after the merger has staged that key+value into the
/// per-merge scratch buffers. Used by consumers that maintain side-state per key
/// (e.g. a bloom filter) so they don't have to re-iterate the merger output.
/// </summary>
/// <remarks>
/// Implemented as a generic struct constraint (<c>TCallback : struct, IHsstTwoByteSlotMergeCallback</c>)
/// so the JIT monomorphises the merger per callback type — the <c>OnKey</c> call resolves
/// to a direct invocation, no virtual dispatch.
/// </remarks>
internal interface IHsstTwoByteSlotMergeCallback
{
    void OnKey(scoped ReadOnlySpan<byte> key);
}
