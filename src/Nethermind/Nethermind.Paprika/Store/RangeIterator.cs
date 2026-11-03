// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Paprika.Crypto;
using Paprika.Data;

namespace Paprika.Store;

/// <summary>
/// Iterates, in ascending key order, the entries of one tree of a read-only batch whose keys lie in a range.
/// </summary>
/// <remarks>
/// <para>
/// It yields exactly the keys of the iterated length for which <see cref="IReadOnlyBatch.TryGet"/> finds a non-empty value,
/// with that value: a bottom page's main map shadows its children, a child's entry is visible only in the nibble range its
/// bottom page routes to it, a data page's own map shadows its side page, and empty values (tombstones) are skipped.
/// </para>
/// <para>
/// Keys have a fixed length of L nibbles and are packed right-aligned, behind a zero nibble when L is odd, so keys and
/// bounds compare bytewise. A relative key at depth d is stored with the oddity of its position, (pad + d) &amp; 1; the page
/// level is not used, as it stops matching the depth after a fan-out. The walk is depth-first and enters only the buckets
/// between the bounds' nibbles, so it seeks to the lower bound. A bottom page is read one child range at a time: the main
/// map's entries in the range and the child's are sorted together, keeping the main map's on equal keys. Memory is one
/// frame per level plus the entries of two pages.
/// </para>
/// <para>
/// One iterator is used by one thread; any number of them may run over one batch. <see cref="CurrentKey"/> and
/// <see cref="CurrentValue"/> are valid until the next <see cref="MoveNext"/>, and the value only while the batch is open.
/// Once the batch is disposed, <see cref="MoveNext"/> and <see cref="CurrentValue"/> throw
/// <see cref="ObjectDisposedException"/>, as do all three once the iterator is.
/// </para>
/// </remarks>
public sealed unsafe class RangeIterator : IDisposable
{
    /// <summary>One frame per level: a key of at most 65 nibbles has at most that many data pages above it.</summary>
    private const int MaxFrames = 72;

    private const int InitialEntries = 256;

    private const int NibblesPerBucketOfStateRoot = 2;

    /// <summary>The ids are fanned out by the first four nibbles of the account.</summary>
    private const int NibblesPerBucketOfIds = 4;

    /// <summary>A storage key in a subtree shared by 16 contracts: the contract's nibble, then the 64 nibbles of the slot.</summary>
    private const int StorageKeyNibbles = 1 + NibblePath.KeccakNibbleCount;

    private const int MaxNibble = NibblePath.NibbleMask;

    /// <summary>
    /// An iterator over nothing. It holds no pooled state, so it is shared and its <see cref="Dispose"/> does nothing.
    /// </summary>
    public static RangeIterator Empty { get; } = new();

    private readonly RootPage _root;
    private readonly IReadOnlyBatchContext _batch = null!;
    private readonly Func<bool>? _isBatchDisposed;
    private readonly bool _contracts;

    /// <summary>The length of the keys, in nibbles.</summary>
    private readonly int _length;

    /// <summary>1 when the length is odd: the packed key starts with a zero nibble.</summary>
    private readonly int _pad;

    private readonly int _keySize;

    /// <summary>The leading bytes of the packed key that are not part of <see cref="CurrentKey"/>: a storage key's contract nibble.</summary>
    private readonly int _keySkip;

    private byte[] _lo = null!;
    private byte[] _hi = null!;

    /// <summary>The packed nibbles of the buckets on the way to the current page.</summary>
    private byte[] _path = null!;

    private Frame[] _frames = null!;
    private int _frameCount;

    /// <summary>A storage subtree, entered at the first <see cref="MoveNext"/>.</summary>
    private DbAddress _pendingRoot;

    /// <summary>The bottom page whose child ranges are being read, if any.</summary>
    private Leaf _leaf;

    private Entry[] _entries = null!;
    private int _entryCount;
    private int _entryIndex;
    private Entry _current;

    /// <summary>The packed keys of the entries.</summary>
    private byte[] _keys = null!;
    private int _keysUsed;

    private bool _disposed;

    private RangeIterator()
    {
    }

    private RangeIterator(RootPage root, IReadOnlyBatchContext batch, Func<bool> isBatchDisposed, int length, bool contracts)
    {
        _root = root;
        _batch = batch;
        _isBatchDisposed = isBatchDisposed;
        _contracts = contracts;
        _length = length;
        _pad = length & 1;
        _keySize = (length + _pad) / NibblePath.NibblePerByte;

        // Only storage keys have an odd length; their first byte holds the pad and the contract's nibble.
        _keySkip = _pad;

        _lo = ArrayPool<byte>.Shared.Rent(_keySize);
        _hi = ArrayPool<byte>.Shared.Rent(_keySize);
        _path = ArrayPool<byte>.Shared.Rent(_keySize);
        _path.AsSpan(0, _keySize).Clear();
        _frames = ArrayPool<Frame>.Shared.Rent(MaxFrames);
        _entries = ArrayPool<Entry>.Shared.Rent(InitialEntries);
        _keys = ArrayPool<byte>.Shared.Rent(InitialEntries * _keySize);
    }

    internal static RangeIterator OverState(RootPage root, IReadOnlyBatchContext batch, Func<bool> isBatchDisposed,
        ReadOnlySpan<byte> from, ReadOnlySpan<byte> to)
    {
        if (from.Length != to.Length || from.Length is 0 or > Keccak.Size)
        {
            throw new ArgumentException("The bounds of a state range must have the same length, from 1 to 32 bytes.");
        }

        if (from.SequenceCompareTo(to) > 0 || root.Data.StateRoot.IsNull)
        {
            return Empty;
        }

        var iterator = new RangeIterator(root, batch, isBatchDisposed, from.Length * NibblePath.NibblePerByte, false);
        from.CopyTo(iterator.Lo);
        to.CopyTo(iterator.Hi);
        iterator.PushFrame(root.Data.StateRoot, FrameKind.StateRoot, 0, NibblesPerBucketOfStateRoot, true, true);
        return iterator;
    }

    internal static RangeIterator OverStorage(RootPage root, IReadOnlyBatchContext batch, Func<bool> isBatchDisposed,
        ReadOnlySpan<byte> account, ReadOnlySpan<byte> from, ReadOnlySpan<byte> to)
    {
        if (account.Length != Keccak.Size || from.Length != Keccak.Size || to.Length != Keccak.Size)
        {
            throw new ArgumentException("The account and the bounds of a storage range must be 32 bytes long.");
        }

        if (from.SequenceCompareTo(to) > 0 || root.TryGetContractId(new Keccak(account), batch, out var id) == false)
        {
            return Empty;
        }

        var subtree = root.Data.Storage.FindStorageSubtree(id, batch, out var contractNibble);
        if (subtree.IsNull)
        {
            return Empty;
        }

        var iterator = new RangeIterator(root, batch, isBatchDisposed, StorageKeyNibbles, false);

        // The bounds are in the subtree's key space, where the contract's nibble precedes the slot.
        iterator.Lo[0] = contractNibble;
        iterator.Hi[0] = contractNibble;
        from.CopyTo(iterator.Lo[1..]);
        to.CopyTo(iterator.Hi[1..]);
        iterator._pendingRoot = subtree;
        return iterator;
    }

    internal static RangeIterator OverContracts(RootPage root, IReadOnlyBatchContext batch, Func<bool> isBatchDisposed,
        ReadOnlySpan<byte> from, ReadOnlySpan<byte> to)
    {
        if (from.Length != Keccak.Size || to.Length != Keccak.Size)
        {
            throw new ArgumentException("The bounds of a contract range must be 32 bytes long.");
        }

        if (from.SequenceCompareTo(to) > 0)
        {
            return Empty;
        }

        var iterator = new RangeIterator(root, batch, isBatchDisposed, NibblePath.KeccakNibbleCount, true);
        from.CopyTo(iterator.Lo);
        to.CopyTo(iterator.Hi);
        iterator.PushFrame(DbAddress.Null, FrameKind.Ids, 0, NibblesPerBucketOfIds, true, true);
        return iterator;
    }

    /// <summary>
    /// The key of the current entry: state keys are as long as the bounds, storage and contract keys are 32 bytes.
    /// </summary>
    public ReadOnlySpan<byte> CurrentKey
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _keys.AsSpan(_current.KeyOffset + _keySkip, _keySize - _keySkip);
        }
    }

    /// <summary>
    /// The value of the current entry as stored; for a contract, its id, 4 bytes little-endian.
    /// </summary>
    public ReadOnlySpan<byte> CurrentValue
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ThrowIfBatchDisposed();
            return new(_current.Value, _current.ValueLength);
        }
    }

    public bool MoveNext()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_isBatchDisposed is null)
        {
            // The shared empty iterator, which must not change.
            return false;
        }

        ThrowIfBatchDisposed();

        while (true)
        {
            if (_entryIndex < _entryCount)
            {
                _current = _entries[_entryIndex++];
                return true;
            }

            _entryIndex = 0;
            _entryCount = 0;
            _keysUsed = 0;

            if (_leaf.Address.IsNull == false)
            {
                GatherNextChildRange();
            }
            else if (Step() == false)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Takes one step of the depth-first walk: reads a data page's local keys, or enters the next bucket of the top frame,
    /// or pops it. A step may gather no entry; only an empty frame stack ends the walk.
    /// </summary>
    private bool Step()
    {
        if (_pendingRoot.IsNull == false)
        {
            var subtree = _pendingRoot;
            _pendingRoot = DbAddress.Null;
            Enter(subtree, 0, true, true);
            return true;
        }

        if (_frameCount == 0)
        {
            return false;
        }

        ref var frame = ref _frames[_frameCount - 1];

        if (frame.LocalPending)
        {
            frame.LocalPending = false;
            GatherLocal(frame);
            return true;
        }

        // Keys of one or no nibble left are local to the page; they are never in a bucket.
        if (_length - frame.Depth < 2 || frame.Next > frame.HiBucket)
        {
            _frameCount--;
            return true;
        }

        var bucket = frame.Next++;
        var child = frame.Kind switch
        {
            FrameKind.StateRoot => new StateRootPage(_batch.GetAt(frame.Address)).GetBucketAddress(bucket),
            FrameKind.Ids => _root.Data.Storage.FindIdsSubtree(bucket, _batch),
            _ => new DataPage(_batch.GetAt(frame.Address)).GetBucketAddress(bucket),
        };

        if (child.IsNull)
        {
            return true;
        }

        for (var i = 0; i < frame.Consumed; i++)
        {
            var nibble = (bucket >> ((frame.Consumed - 1 - i) * NibblePath.NibbleShift)) & NibblePath.NibbleMask;
            SetNibble(_path, _pad + frame.Depth + i, nibble);
        }

        Enter(child, frame.Depth + frame.Consumed, frame.OnLo && bucket == frame.LoBucket,
            frame.OnHi && bucket == frame.HiBucket);
        return true;
    }

    /// <summary>
    /// Enters a page: a data page becomes a frame, a bottom page the one whose child ranges are read next, and a child
    /// bottom page has its entries gathered at once.
    /// </summary>
    /// <param name="depth">The number of nibbles of the key above the page.</param>
    /// <param name="onLo">Whether the nibbles above the page are those of the lower bound.</param>
    /// <param name="onHi">Whether the nibbles above the page are those of the upper bound.</param>
    private void Enter(DbAddress address, int depth, bool onLo, bool onHi)
    {
        var page = _batch.GetAt(address);

        switch (page.Header.PageType)
        {
            case PageType.DataPage:
                PushFrame(address, FrameKind.Data, depth, new DataPage(page).ConsumedNibbles, onLo, onHi);
                break;
            case PageType.Bottom:
                _leaf = new Leaf
                {
                    Address = address,
                    Depth = depth,
                    OnLo = onLo,
                    OnHi = onHi,
                    NextNibble = FirstNibbleOf(depth, onLo),
                    LastNibble = LastNibbleOf(depth, onHi),
                };
                break;
            case PageType.ChildBottom:
                foreach (var item in new ChildBottomPage(page).Map.EnumerateAll())
                {
                    Add(item.Key, item.RawData, depth, FirstNibbleOf(depth, onLo), LastNibbleOf(depth, onHi), onLo, onHi, 0);
                }

                KeepVisible();
                break;
            default:
                throw new InvalidOperationException(
                    $"The page at {address} has the type {page.Header.PageType}, which a range iteration does not expect.");
        }
    }

    private void PushFrame(DbAddress address, FrameKind kind, int depth, int consumed, bool onLo, bool onHi)
    {
        ref var frame = ref _frames[_frameCount++];
        frame = new Frame
        {
            Address = address,
            Kind = kind,
            Depth = depth,
            Consumed = consumed,
            OnLo = onLo,
            OnHi = onHi,
            LocalPending = kind == FrameKind.Data && _length - depth <= 1,
        };

        if (_length - depth >= 2)
        {
            frame.LoBucket = onLo ? Nibbles(Lo, depth, consumed) : 0;
            frame.HiBucket = onHi ? Nibbles(Hi, depth, consumed) : (1 << (consumed * NibblePath.NibbleShift)) - 1;
            frame.Next = frame.LoBucket;
        }
    }

    /// <summary>
    /// Gathers the active bottom page's entries of its next child range that holds any visible one, or deactivates it.
    /// </summary>
    private void GatherNextChildRange()
    {
        ref var leaf = ref _leaf;
        var page = new BottomPage(_batch.GetAt(leaf.Address));

        if (_length == leaf.Depth)
        {
            // The empty key lives only in the main map.
            foreach (var item in page.Map.EnumerateAll())
            {
                Add(item.Key, item.RawData, leaf.Depth, 0, MaxNibble, leaf.OnLo, leaf.OnHi, 0);
            }

            KeepVisible();
            leaf = default;
            return;
        }

        while (leaf.NextNibble <= leaf.LastNibble)
        {
            // A child holds the keys from its index up to the next allocated child, as BottomPage.TryGet routes them.
            var first = leaf.NextNibble;
            var end = first;
            while (end < MaxNibble && page.GetChildAddress(end + 1).IsNull)
            {
                end++;
            }

            var last = Math.Min(end, leaf.LastNibble);
            leaf.NextNibble = end + 1;

            for (var nibble = first; nibble <= last; nibble++)
            {
                foreach (var item in page.Map.EnumerateNibble((byte)nibble))
                {
                    Add(item.Key, item.RawData, leaf.Depth, first, last, leaf.OnLo, leaf.OnHi, 0);
                }
            }

            var child = page.FindMatchingChild(first);
            if (child >= 0)
            {
                var childMap = new ChildBottomPage(_batch.GetAt(page.GetChildAddress(child))).Map;
                foreach (var item in childMap.EnumerateAll())
                {
                    Add(item.Key, item.RawData, leaf.Depth, first, last, leaf.OnLo, leaf.OnHi, 1);
                }
            }

            KeepVisible();
            if (_entryCount > 0)
            {
                return;
            }
        }

        leaf = default;
    }

    /// <summary>
    /// Gathers the keys local to a data page, those with one or no nibble left, through the page's own lookup, which
    /// also reads its side page.
    /// </summary>
    private void GatherLocal(in Frame frame)
    {
        var page = new DataPage(_batch.GetAt(frame.Address));
        ReadOnlySpan<byte> value;

        if (_length == frame.Depth)
        {
            if (page.TryGet(_batch, NibblePath.Empty, out value))
            {
                Add(NibblePath.Empty, value, frame.Depth, 0, MaxNibble, frame.OnLo, frame.OnHi, 0);
            }
        }
        else
        {
            var last = LastNibbleOf(frame.Depth, frame.OnHi);
            for (var nibble = FirstNibbleOf(frame.Depth, frame.OnLo); nibble <= last; nibble++)
            {
                var key = NibblePath.Single((byte)nibble, (_pad + frame.Depth) & 1);
                if (page.TryGet(_batch, key, out value))
                {
                    Add(key, value, frame.Depth, nibble, nibble, frame.OnLo, frame.OnHi, 0);
                }
            }
        }

        KeepVisible();
    }

    /// <summary>
    /// Adds an entry if its key, relative to a page at <paramref name="depth"/>, has the iterated length, starts with a
    /// nibble in [<paramref name="firstNibble"/>, <paramref name="lastNibble"/>] and is within the bounds.
    /// </summary>
    /// <param name="precedence">0 for a main map, whose entry shadows a child's (1) of the same key.</param>
    private void Add(scoped in NibblePath key, ReadOnlySpan<byte> value, int depth, int firstNibble, int lastNibble,
        bool onLo, bool onHi, byte precedence)
    {
        if (key.Length != _length - depth || (key.Length > 0 && (key.Nibble0 < firstNibble || key.Nibble0 > lastNibble)))
        {
            return;
        }

        EnsureCapacity();

        var packed = _keys.AsSpan(_keysUsed, _keySize);
        _path.AsSpan(0, _keySize).CopyTo(packed);
        if (key.Length > 0)
        {
            WriteNibbles(packed, _pad + depth, key);
        }

        if ((onLo && packed.SequenceCompareTo(Lo) < 0) || (onHi && packed.SequenceCompareTo(Hi) > 0))
        {
            return;
        }

        _entries[_entryCount++] = new Entry
        {
            KeyOffset = _keysUsed,
            ValueLength = value.Length,
            Value = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(value)),
            Precedence = precedence,
        };
        _keysUsed += _keySize;
    }

    /// <summary>
    /// Writes the nibbles of <paramref name="key"/> from the packed position <paramref name="at"/> to the end.
    /// </summary>
    private static void WriteNibbles(Span<byte> packed, int at, scoped in NibblePath key)
    {
        if (key.Odd != (at & 1))
        {
            for (var i = 0; i < key.Length; i++)
            {
                SetNibble(packed, at + i, key.GetAt(i));
            }

            return;
        }

        // Aligned: the raw bytes end with the packed key. An odd start keeps the path's nibble before it.
        var start = at / NibblePath.NibblePerByte;
        var before = packed[start];
        key.RawSpan.CopyTo(packed[start..]);
        if ((at & 1) == 1)
        {
            packed[start] = (byte)((before & 0xF0) | (packed[start] & NibblePath.NibbleMask));
        }
    }

    /// <summary>
    /// Sorts the gathered entries and keeps the visible ones: of equal keys the first, a main map's, unless it is a
    /// tombstone; for contracts, only valid ids.
    /// </summary>
    private void KeepVisible()
    {
        var entries = _entries.AsSpan(0, _entryCount);
        entries.Sort(new EntryComparer(_keys, _keySize));

        var kept = 0;
        for (var i = 0; i < entries.Length; i++)
        {
            // Compacting writes only at or before i, so entries[i - 1] is still the sorted predecessor.
            if (i > 0 && KeyOf(entries[i - 1]).SequenceEqual(KeyOf(entries[i])))
            {
                continue;
            }

            var entry = entries[i];
            if (entry.ValueLength == 0 || (_contracts && IsValidId(entry) == false))
            {
                continue;
            }

            entries[kept++] = entry;
        }

        _entryCount = kept;
    }

    /// <summary>
    /// Whether the value is a contract id that <see cref="StorageFanOut.Level0.TryGetId"/> reads. The id 0 was written by
    /// <see cref="IBatch.Destroy"/>, which every destroyed account shared.
    /// </summary>
    private static bool IsValidId(in Entry entry) =>
        entry.ValueLength >= sizeof(uint) &&
        BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(entry.Value, sizeof(uint))) != ContractId.Null.Value;

    private ReadOnlySpan<byte> KeyOf(in Entry entry) => _keys.AsSpan(entry.KeyOffset, _keySize);

    private void EnsureCapacity()
    {
        if (_entryCount == _entries.Length)
        {
            _entries = Grow(_entries, _entries.Length * 2);
        }

        if (_keysUsed + _keySize > _keys.Length)
        {
            _keys = Grow(_keys, _keys.Length * 2);
        }
    }

    private static T[] Grow<T>(T[] array, int length)
    {
        var grown = ArrayPool<T>.Shared.Rent(length);
        array.CopyTo(grown, 0);
        ArrayPool<T>.Shared.Return(array);
        return grown;
    }

    private Span<byte> Lo => _lo.AsSpan(0, _keySize);

    private Span<byte> Hi => _hi.AsSpan(0, _keySize);

    private int FirstNibbleOf(int depth, bool onLo) => onLo && _length > depth ? NibbleAt(Lo, depth) : 0;

    private int LastNibbleOf(int depth, bool onHi) => onHi && _length > depth ? NibbleAt(Hi, depth) : MaxNibble;

    /// <summary>The <paramref name="count"/> nibbles of a packed key from its nibble <paramref name="index"/>, as a number.</summary>
    private int Nibbles(ReadOnlySpan<byte> packed, int index, int count)
    {
        var value = 0;
        for (var i = 0; i < count; i++)
        {
            value = (value << NibblePath.NibbleShift) | NibbleAt(packed, index + i);
        }

        return value;
    }

    private int NibbleAt(ReadOnlySpan<byte> packed, int index)
    {
        var at = _pad + index;
        var b = packed[at / NibblePath.NibblePerByte];
        return (at & 1) == 0 ? b >> NibblePath.NibbleShift : b & NibblePath.NibbleMask;
    }

    private static void SetNibble(Span<byte> packed, int at, int nibble)
    {
        ref var b = ref packed[at / NibblePath.NibblePerByte];
        b = (at & 1) == 0
            ? (byte)((b & NibblePath.NibbleMask) | (nibble << NibblePath.NibbleShift))
            : (byte)((b & 0xF0) | nibble);
    }

    private void ThrowIfBatchDisposed()
    {
        if (_isBatchDisposed?.Invoke() == true)
        {
            // The root copy and the pages it reaches may already serve another batch.
            throw new ObjectDisposedException(nameof(IReadOnlyBatch), "The batch this iterator reads has been disposed.");
        }
    }

    public void Dispose()
    {
        // The shared empty iterator holds no pooled arrays.
        if (_disposed || _frames is null)
        {
            return;
        }

        _disposed = true;

        ArrayPool<byte>.Shared.Return(_lo);
        ArrayPool<byte>.Shared.Return(_hi);
        ArrayPool<byte>.Shared.Return(_path);
        ArrayPool<Frame>.Shared.Return(_frames);
        ArrayPool<Entry>.Shared.Return(_entries);
        ArrayPool<byte>.Shared.Return(_keys);
    }

    private enum FrameKind : byte
    {
        StateRoot,

        /// <summary>The 65,536 ids subtrees, one per first four nibbles of the account; there is no page for it.</summary>
        Ids,

        Data,
    }

    private struct Frame
    {
        public DbAddress Address;
        public FrameKind Kind;
        public int Depth;

        /// <summary>The nibbles per bucket.</summary>
        public int Consumed;

        public bool OnLo;
        public bool OnHi;
        public int LoBucket;
        public int HiBucket;
        public int Next;

        /// <summary>Whether the local keys of the data page are still to be read.</summary>
        public bool LocalPending;
    }

    private struct Leaf
    {
        public DbAddress Address;
        public int Depth;
        public bool OnLo;
        public bool OnHi;
        public int NextNibble;
        public int LastNibble;
    }

    private struct Entry
    {
        public int KeyOffset;
        public int ValueLength;

        /// <summary>The value in page memory, which stays as it is while the batch lives.</summary>
        public byte* Value;

        public byte Precedence;
    }

    private readonly struct EntryComparer(byte[] keys, int keySize) : IComparer<Entry>
    {
        public int Compare(Entry x, Entry y)
        {
            var byKey = keys.AsSpan(x.KeyOffset, keySize).SequenceCompareTo(keys.AsSpan(y.KeyOffset, keySize));
            return byKey != 0 ? byKey : x.Precedence.CompareTo(y.Precedence);
        }
    }
}
