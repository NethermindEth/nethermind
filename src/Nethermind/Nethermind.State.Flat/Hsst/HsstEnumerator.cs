// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State.Flat.Hsst.BTree;
using Nethermind.State.Flat.Hsst.PackedArray;
using Nethermind.State.Flat.Hsst.TwoByteSlot;

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Cursor-based forward enumerator over an HSST scope, optimised for N-way merge.
/// Class-based — not a ref struct — so callers can put many of these into an array
/// and round-robin them in a sort-merge.
///
/// Generic on <typeparamref name="TReader"/> / <typeparamref name="TPin"/> so the
/// enumerator can address scopes anywhere in a long-offset reader (e.g. an mmap
/// view spanning more than 2 GiB) without losing precision. Internal offsets are
/// stored as <see cref="long"/> absolute positions; public <see cref="Bound"/>s
/// returned by <see cref="CurrentValue"/> are reader-absolute. The current key is
/// only exposed via <see cref="CurrentKeyLength"/> + <see cref="CopyCurrentLogicalKey"/>
/// so callers cannot accidentally consume the on-disk LE-stored layout (see PackedArray
/// LE-stored note on <see cref="HsstPackedArrayBuilder{TWriter}"/>).
///
/// The constructor selects exactly one layout-specific variant based on the trailing
/// <see cref="IndexType"/> byte and stores it in a typed field; the other variant fields
/// remain null. Each public method dispatches via a <c>switch</c> on a discriminator.
///
///   - <see cref="IndexType.PackedArray"/>     → <see cref="HsstPackedArrayEnumerator{TReader,TPin}"/> (no offset table; fixed stride).
///   - <see cref="IndexType.BTree"/>           → <see cref="HsstBTreeEnumerator{TReader,TPin}"/>       (offset table; leaves only reachable by recursing the index tree).
///
/// The keys-first two-byte-slot variants (<see cref="IndexType.TwoByteSlotValue"/> /
/// <see cref="IndexType.TwoByteSlotValueLarge"/>) carry their <see cref="IndexType"/> byte
/// at byte 0, not the tail; they are always nested and opened via
/// <see cref="CreateTwoByteSlot"/>, which dispatches forward with no tail read.
///
/// <see cref="MoveNext"/> consumes the reader (variants need it for LEB128 / Ends-array
/// reads) and caches the current key/value bounds. Subsequent <see cref="CurrentKeyLength"/>
/// access is a property read; <see cref="GetCurrentValue"/> takes the reader only to
/// materialise a pinned span (no decode). The enumerator stores only integer offsets,
/// never key/value bytes.
/// </summary>
public struct HsstEnumerator<TReader, TPin> : IDisposable
    where TPin : struct, IBufferPin, allows ref struct
    where TReader : IHsstByteReader<TPin>, allows ref struct
{
    private enum VariantKind : byte { Empty, PackedArray, BTree, BTreeKeyFirst, TwoByteSlot }

    // Struct envelope: only thing that needs to live on the value is the
    // discriminator and the variant references. All mutable
    // iteration state lives on the heap-allocated variant objects, so copies
    // of this struct (e.g. via ArrayPoolList<T>'s by-value indexer) still
    // observe / advance the same underlying cursor.
    //
    // default(HsstEnumerator) has _kind == Empty, so MoveNext returns false and
    // Current is empty. Callers like PersistedSnapshotScanner's enumerators rely on
    // this when they reset a field to `default` between nested scopes.
    private readonly VariantKind _kind;
    private readonly HsstPackedArrayEnumerator<TReader, TPin>? _packed;
    private readonly HsstBTreeEnumerator<TReader, TPin>? _btree;
    private readonly HsstTwoByteSlotValueEnumerator<TReader, TPin>? _tbsv;

    public HsstEnumerator(scoped in TReader reader, Bound scope)
    {
        if (scope.Length < 2)
        {
            _kind = VariantKind.Empty;
            return;
        }

        // Last byte of the HSST is the IndexType byte.
        IndexType tag;
        using (TPin tagPin = reader.PinBuffer(new Bound(scope.Offset + scope.Length - 1, 1)))
        {
            tag = (IndexType)tagPin.Buffer[0];
        }


        switch (tag)
        {
            case IndexType.PackedArray:
                _packed = HsstPackedArrayEnumerator<TReader, TPin>.TryCreate(in reader, scope);
                _kind = _packed is not null ? VariantKind.PackedArray : VariantKind.Empty;
                break;
            case IndexType.BTree:
                _btree = new HsstBTreeEnumerator<TReader, TPin>(in reader, scope, keyFirst: false);
                _kind = VariantKind.BTree;
                break;
            case IndexType.BTreeKeyFirst:
                _btree = new HsstBTreeEnumerator<TReader, TPin>(in reader, scope, keyFirst: true);
                _kind = VariantKind.BTreeKeyFirst;
                break;
            // DenseByteIndex is used for the persisted-snapshot outer + per-address
            // containers, which the merge code accesses directly via TryGet rather
            // than via this enumerator. TwoByteSlotValue / TwoByteSlotValueLarge lead
            // with their IndexType byte (byte 0), never the tail — they are nested-only
            // and opened via CreateTwoByteSlot, so this last-byte dispatch never resolves
            // them. Defensive empty enumeration: never invoked in production paths but
            // avoids crashing the BTree parser if the trailer ever reaches this constructor.
            default:
                _kind = VariantKind.Empty;
                break;
        }
    }

    /// <summary>
    /// Front-dispatch constructor for the keys-first two-byte-slot variants, whose
    /// <see cref="IndexType"/> byte leads the blob at byte 0. Used by
    /// <see cref="CreateTwoByteSlot"/>; non-two-byte-slot <paramref name="frontTag"/>
    /// values yield an empty enumerator.
    /// </summary>
    private HsstEnumerator(scoped in TReader reader, Bound scope, IndexType frontTag)
    {
        switch (frontTag)
        {
            case IndexType.TwoByteSlotValue:
                _tbsv = HsstTwoByteSlotValueEnumerator<TReader, TPin>.TryCreate(in reader, scope, offsetSize: 2);
                _kind = _tbsv is not null ? VariantKind.TwoByteSlot : VariantKind.Empty;
                break;
            case IndexType.TwoByteSlotValueLarge:
                _tbsv = HsstTwoByteSlotValueEnumerator<TReader, TPin>.TryCreate(in reader, scope, offsetSize: 3);
                _kind = _tbsv is not null ? VariantKind.TwoByteSlot : VariantKind.Empty;
                break;
            default:
                _kind = VariantKind.Empty;
                break;
        }
    }

    /// <summary>
    /// Open an enumerator over a nested keys-first two-byte-slot HSST scope
    /// (<see cref="IndexType.TwoByteSlotValue"/> / <see cref="IndexType.TwoByteSlotValueLarge"/>).
    /// Dispatches on the leading <see cref="IndexType"/> byte (byte 0) — no tail read. The
    /// caller must already know <paramref name="scope"/> is one of these two variants.
    /// </summary>
    public static HsstEnumerator<TReader, TPin> CreateTwoByteSlot(scoped in TReader reader, Bound scope)
    {
        // 5 = smallest valid two-byte-slot blob (1 IndexType + 2 KeyCount + 2 key).
        if (scope.Length < 5) return default;

        IndexType tag;
        using (TPin tagPin = reader.PinBuffer(new Bound(scope.Offset, 1)))
        {
            tag = (IndexType)tagPin.Buffer[0];
        }
        return new HsstEnumerator<TReader, TPin>(in reader, scope, tag);
    }

    public long Count => _kind switch
    {
        VariantKind.PackedArray => _packed!.Count,
        VariantKind.BTree => _btree!.Count,
        VariantKind.BTreeKeyFirst => _btree!.Count,
        VariantKind.TwoByteSlot => _tbsv!.Count,
        _ => 0,
    };

    public bool MoveNext(scoped in TReader reader) => _kind switch
    {
        VariantKind.PackedArray => _packed!.MoveNext(),
        VariantKind.BTree => _btree!.MoveNext(in reader),
        VariantKind.BTreeKeyFirst => _btree!.MoveNext(in reader),
        VariantKind.TwoByteSlot => _tbsv!.MoveNext(in reader),
        _ => false,
    };

    /// <summary>
    /// Reader-absolute bound of the current key. Private: callers must go through
    /// <see cref="CopyCurrentLogicalKey"/> so the LE-stored PackedArray layout
    /// stays an internal concern of this enumerator.
    /// </summary>
    private Bound CurrentKey => _kind switch
    {
        VariantKind.PackedArray => _packed!.CurrentKey,
        VariantKind.BTree => _btree!.CurrentKey,
        VariantKind.BTreeKeyFirst => _btree!.CurrentKey,
        VariantKind.TwoByteSlot => _tbsv!.CurrentKey,
        _ => default,
    };

    /// <summary>Length of the current key in bytes. Use to size the <c>dst</c> buffer for <see cref="CopyCurrentLogicalKey"/>.</summary>
    public long CurrentKeyLength => CurrentKey.Length;

    /// <summary>
    /// Copy the current key in its LOGICAL (lex/BE) form into <paramref name="dst"/> and
    /// return that slice. For BTree and BE-stored PackedArray the stored
    /// bytes already match logical form, so this is a straight copy. For LE-stored
    /// PackedArray (auto-enabled at <c>keySize ∈ {2,4,8}</c>) the on-disk bytes are
    /// byte-reversed and this method un-reverses them — callers see the same lex/BE
    /// bytes that were originally <c>Add</c>ed to the builder, regardless of layout.
    /// <paramref name="dst"/> must be at least <see cref="CurrentKeyLength"/> long.
    /// </summary>
    public ReadOnlySpan<byte> CopyCurrentLogicalKey(scoped in TReader reader, Span<byte> dst)
    {
        Bound b = CurrentKey;
        int len = (int)b.Length;
        Span<byte> outSpan = dst[..len];
        using TPin pin = reader.PinBuffer(b);
        ReadOnlySpan<byte> stored = pin.Buffer;
        // LE-stored variants byte-reverse on the way out so callers see the original
        // BE/lex input bytes. PackedArray opts in via IsLittleEndian; the two
        // TwoByteSlotValue formats always store LE.
        bool reverse = (_kind == VariantKind.PackedArray && _packed!.IsLittleEndian)
            || _kind == VariantKind.TwoByteSlot;
        if (reverse)
        {
            for (int i = 0; i < len; i++) outSpan[i] = stored[len - 1 - i];
        }
        else
        {
            stored.CopyTo(outSpan);
        }
        return outSpan;
    }

    /// <summary>Pin the current value bytes via <paramref name="reader"/>; empty pin when length is 0.</summary>
    public TPin GetCurrentValue(scoped in TReader reader)
    {
        Bound b = CurrentValue;
        return reader.PinBuffer(b);
    }

    public Bound CurrentValue => _kind switch
    {
        VariantKind.PackedArray => _packed!.CurrentValue,
        VariantKind.BTree => _btree!.CurrentValue,
        VariantKind.BTreeKeyFirst => _btree!.CurrentValue,
        VariantKind.TwoByteSlot => _tbsv!.CurrentValue,
        _ => default,
    };

    public long CurrentMetadataStart => _kind switch
    {
        VariantKind.PackedArray => _packed!.CurrentMetadataStart,
        VariantKind.BTree => _btree!.CurrentMetadataStart,
        VariantKind.BTreeKeyFirst => _btree!.CurrentMetadataStart,
        VariantKind.TwoByteSlot => _tbsv!.CurrentMetadataStart,
        _ => 0,
    };

    // No variant holds releasable resources today (HsstBTreeEnumerator's leaf buffer is
    // managed memory). Kept on IDisposable so callers can stay on `using`; if a variant
    // later acquires resources, plumb the release through here.
    public void Dispose() { }

}

