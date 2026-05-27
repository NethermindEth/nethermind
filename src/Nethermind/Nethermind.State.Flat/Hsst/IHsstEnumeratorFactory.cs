// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Stateless dispatcher used by <see cref="NWayMergeCursor{TReader,TPin,TSource,TFactory}"/>
/// to construct an <see cref="HsstEnumerator{TReader,TPin}"/> over a per-source bound during
/// cursor construction. Concrete implementations dispatch over the two HSST layout entry
/// points: the tail-byte <see cref="IndexType"/> form (PackedArray / BTree / BTreeKeyFirst)
/// and the front-byte two-byte-slot form (TwoByteSlotValue / TwoByteSlotValueLarge).
/// </summary>
/// <remarks>
/// Implementations are zero-allocation struct types; the cursor's generic substitution
/// monomorphises the call so <see cref="Create"/> resolves to a direct invocation.
/// </remarks>
internal interface IHsstEnumeratorFactory<TReader, TPin>
    where TPin : struct, IBufferPin, allows ref struct
    where TReader : IHsstByteReader<TPin>, allows ref struct
{
    HsstEnumerator<TReader, TPin> Create(scoped in TReader reader, Bound bound);
}
