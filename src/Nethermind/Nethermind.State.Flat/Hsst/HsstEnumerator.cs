// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Utils;

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Forward-only walker over an HSST scope. Yields entries in sorted key order.
/// Generic over the same <typeparamref name="TReader"/>/<typeparamref name="TPin"/> as
/// <see cref="HsstReader{TReader,TPin}"/>; constructed from a <see cref="Bound"/> that
/// scopes which HSST is being enumerated.
///
/// Thin ref-struct wrapper around <see cref="HsstMergeEnumerator{TReader,TPin}"/> that
/// stores the reader so callers don't have to pass it on every <see cref="MoveNext"/>.
/// All layout-specific iteration (PackedArray / ByteTagMap / BTree) lives on the merge
/// enumerator's variants — for BTree this means eagerly collecting every leaf entry
/// offset at construction time.
///
/// Both <c>Current.KeyBound</c> and <c>Current.ValueBound</c> are absolute reader offsets;
/// callers slice them out of their own data span (or pin them via the reader). Bounds
/// stay valid for the reader's lifetime — no per-MoveNext invalidation, since neither
/// involves enumerator-owned storage.
/// </summary>
public ref struct HsstEnumerator<TReader, TPin>(scoped in TReader reader, Bound bound) : IDisposable
    where TPin : struct, IBufferPin, allows ref struct
    where TReader : IHsstByteReader<TPin>, allows ref struct
{
    private TReader _reader = reader;
    private readonly HsstMergeEnumerator<TReader, TPin> _inner = new(in reader, bound);

    // Callers (e.g. PersistedSnapshotScanner.StorageEnumerator) park enumerators as
    // zero-initialised struct fields and reset them with `= default` between uses, so
    // _inner can be null. Treat that as an exhausted enumerator.
    public bool MoveNext() => _inner is not null && _inner.MoveNext(in _reader);

    public readonly KeyValueEntry Current =>
        _inner is null ? default : new(_inner.CurrentKey, _inner.CurrentValue);

    public void Dispose() => _inner?.Dispose();
}

/// <summary>
/// One key/value pair yielded by <see cref="HsstEnumerator{TReader,TPin}.Current"/>. Both
/// fields are absolute reader offset+length tuples; callers slice them out of the underlying
/// data span (or pin via the reader). Both bounds stay valid for the reader's lifetime —
/// no per-MoveNext invalidation, since neither involves enumerator-owned storage.
/// </summary>
public readonly ref struct KeyValueEntry(Bound keyBound, Bound valueBound)
{
    public Bound KeyBound { get; } = keyBound;
    public Bound ValueBound { get; } = valueBound;
}
