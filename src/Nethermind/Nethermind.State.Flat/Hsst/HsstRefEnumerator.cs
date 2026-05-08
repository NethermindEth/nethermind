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
/// Thin ref-struct wrapper around <see cref="HsstEnumerator{TReader,TPin}"/> that
/// stores the reader so callers don't have to pass it on every <see cref="MoveNext"/>.
/// All layout-specific iteration (PackedArray / ByteTagMap / BTree) lives on the merge
/// enumerator's variants. Construction is cheap — for BTree it only records the scope
/// bounds (<see cref="HsstEnumerator{TReader,TPin}"/>'s <c>BTreeVariant</c> ctor); the
/// actual tree walk happens lazily on each <see cref="MoveNext"/>, descending one leaf
/// at a time and buffering that leaf's metaStart pointers in a reusable array.
///
/// <c>Current.ValueBound</c> is an absolute reader offset; callers slice it out of their
/// own data span (or pin it via the reader). The current key is exposed only through
/// <see cref="CopyCurrentLogicalKey"/> + <see cref="KeyValueEntry.KeyLength"/> so the
/// LE-stored PackedArray layout stays an internal concern of the enumerator. Bounds
/// stay valid for the reader's lifetime — no per-MoveNext invalidation, since neither
/// involves enumerator-owned storage.
/// </summary>
public ref struct HsstRefEnumerator<TReader, TPin>(scoped in TReader reader, Bound bound) : IDisposable
    where TPin : struct, IBufferPin, allows ref struct
    where TReader : IHsstByteReader<TPin>, allows ref struct
{
    private TReader _reader = reader;
    private HsstEnumerator<TReader, TPin> _inner = new(in reader, bound);

    // _inner is a struct now: default(HsstRefEnumerator) gives default(HsstEnumerator)
    // whose _kind is Empty, so MoveNext returns false and Current is empty — which is
    // the behaviour callers like PersistedSnapshotScanner.StorageEnumerator rely on
    // when they reset the field to `default` between uses.
    public bool MoveNext() => _inner.MoveNext(in _reader);

    public readonly KeyValueEntry Current => new(_inner.CurrentKeyLength, _inner.CurrentValue);

    /// <summary>
    /// Copy the current key in its logical (lex/BE) form into <paramref name="dst"/>.
    /// See <see cref="HsstEnumerator{TReader,TPin}.CopyCurrentLogicalKey"/>.
    /// </summary>
    public readonly ReadOnlySpan<byte> CopyCurrentLogicalKey(Span<byte> dst)
        => _inner.CopyCurrentLogicalKey(in _reader, dst);

    public void Dispose() => _inner.Dispose();
}

/// <summary>
/// One key/value pair yielded by <see cref="HsstRefEnumerator{TReader,TPin}.Current"/>.
/// <see cref="ValueBound"/> is an absolute reader offset+length tuple; callers slice it
/// out of the underlying data span (or pin via the reader). The current key is exposed
/// only as <see cref="KeyLength"/> + <see cref="HsstRefEnumerator{TReader,TPin}.CopyCurrentLogicalKey"/>
/// so the LE-stored PackedArray layout stays an internal concern of the enumerator. The
/// value bound stays valid for the reader's lifetime — no per-MoveNext invalidation,
/// since it doesn't involve enumerator-owned storage.
/// </summary>
public readonly ref struct KeyValueEntry(long keyLength, Bound valueBound)
{
    public long KeyLength { get; } = keyLength;
    public Bound ValueBound { get; } = valueBound;
}
