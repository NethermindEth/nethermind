// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.State.Flat.Hsst.BTree;

/// <summary>
/// Reads a B-tree index block. An index block stores sorted key-value pairs with a
/// fixed-width metadata header at the front, followed by the keys and values sections.
///
/// Layout (low → high address):
///   [Flags: u8][KeyCount: u16 LE][KeySize: u16 LE][CommonPrefixLen: u8][BaseOffset: 6-byte LE]
///   [Keys section][Values section]
///
/// Header is a fixed 12 bytes. <c>BaseOffset</c> sits at the end of the header so the
/// fields needed to parse keys (KeyCount, KeySize, KeyType / IsKeyLittleEndian from Flags,
/// CommonPrefixLen) group into the first 6 bytes; BaseOffset is only consumed by
/// <see cref="GetUInt64Value"/> after a successful floor match.
///
/// Flags: bits 0-1 = <see cref="BTreeNodeKind"/> (00=Entry, 01=Leaf, 10=Intermediate, 11=reserved),
/// bits 2-3 = KeyType, bits 4-5 = ValueSizeCode, bit 6 = IsKeyLittleEndian. Bit 7 is reserved.
/// The same Flags byte appears at the front of every addressable thing — data-region entries
/// (NodeKind = Entry, bits 2-7 = 0) and BTreeNode nodes (NodeKind = Leaf | Intermediate) —
/// so the BTree reader can dispatch on a single byte read without consulting the parent.
///
/// ValueSizeCode (bits 4-5) packs the per-entry value width into 2 bits: 00→2, 01→3,
/// 10→4, 11→6. There is no Variable-value shape for b-tree index nodes; widths outside
/// the supported set are not encodable.
///
/// IsKeyLittleEndian (bit 6) marks that fixed-width key slots are stored byte-reversed so an
/// x86 LE integer load of a slot equals its semantic numeric/lex value. Set for Uniform
/// with KeySize ∈ {2,4,8}, and unconditionally for Variable (KeyType=0) where the prefixArr
/// is uniformly 2 bytes/slot — the SIMD floor scan exploits this to drop its per-lane
/// byte-swap shuffle. Stored slots are LE-reversed under this flag; <see cref="GetFullKey"/>
/// always emits lex/original-order bytes.
///
/// All header fields are fixed-width — no varint decoding on parse. With the 64 KiB
/// node-size cap, every count/size field fits in u16. Header at the front lets the hardware
/// prefetcher pull the keys/values forward into cache while the search code is still parsing
/// the header.
///
/// KeyType:
///   0 = Variable: SoA layout — [prefixArr: N×u16 LE][offsetArr: N×u16 LE][remainingkeys].
///       prefixArr[i] holds the first 2 bytes of key i, byte-reversed (LE-stored) so a
///       u16 LE load yields a value with the same unsigned-int order as a lex compare on
///       the original 2-byte prefix. offsetArr[i] = (lenTag &lt;&lt; 14) | tailOffset:
///       tag 00=len 0, 01=len 1, 10=len 2 (no tail), 11=len ≥ 3 (tail at tailOffset in
///       remainingkeys; tail length sentinel-derived from offsetArr[i+1].tailOffset, with
///       the implicit sentinel for i=N being remainingkeys.Length). Tags 00/01/10 freeze
///       the cursor (offset == next tag-11 entry's offset). 14-bit tailOffset caps
///       remainingkeys at 16 KiB per section.
///   1 = Uniform: packed fixed-width entries.
///
/// When CommonPrefixLen &gt; 0 every stored key equals (CommonKeyPrefix || stored slot i);
/// the keys section holds suffixes only — use <see cref="GetFullKey"/> to reconstruct lex
/// bytes. The actual prefix bytes are supplied by the caller via
/// <see cref="ReadFromStart"/>'s <c>parentSeparator</c> parameter, which the descent loop
/// derives from the parent's matched separator (or, for the root, from the HSST trailer).
/// The builder guarantees that each separator length is at least the child's prefix length,
/// so the first <c>CommonPrefixLen</c> bytes of the parent's full separator are the child's
/// prefix bytes.
/// </summary>
public readonly ref struct BTreeNodeReader(
    NodeMetadata metadata,
    ReadOnlySpan<byte> values,
    ReadOnlySpan<byte> keys,
    ReadOnlySpan<byte> commonKeyPrefix,
    int totalSize)
{
    // Ref-like primary-ctor params can't be used in instance members of a ref struct;
    // forward them into fields.
    private readonly ReadOnlySpan<byte> values = values;
    private readonly ReadOnlySpan<byte> keys = keys;
    private readonly ReadOnlySpan<byte> commonKeyPrefix = commonKeyPrefix;

    public int EntryCount => metadata.KeyCount;
    public BTreeNodeKind NodeKind => metadata.NodeKind;
    public NodeMetadata Metadata => metadata;
    /// <summary>Total bytes occupied by this index node, including header.</summary>
    public int TotalSize => totalSize;

    /// <summary>
    /// Bytes shared by every stored key. Empty when the node was written without the
    /// common-prefix optimization. The full lex-order key for entry i is reconstructed via
    /// <see cref="GetFullKey"/>.
    /// </summary>
    public ReadOnlySpan<byte> CommonKeyPrefix => commonKeyPrefix;

    /// <summary>
    /// Read an index block forward from <paramref name="nodeStart"/> (inclusive start position).
    /// <paramref name="parentSeparator"/> supplies the common-key-prefix bytes for nodes whose
    /// header records a non-zero <c>CommonPrefixLen</c>. Must be the full lex-order separator
    /// bytes the parent used to route into this node — the builder guarantees
    /// <c>parentSeparator.Length &gt;= CommonPrefixLen</c>. Pass <c>default</c> when the caller
    /// only needs value-only access (e.g. <see cref="HsstEnumerator{TReader,TPin}"/>): the
    /// prefix-dependent paths (<see cref="TryGetFloor"/>, <see cref="GetFullKey"/>,
    /// <see cref="GetSeparatorBytes"/>) will misbehave but <see cref="GetUInt64Value"/>,
    /// <see cref="EntryCount"/>, and friends still work.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static BTreeNodeReader ReadFromStart(ReadOnlySpan<byte> data, int nodeStart, ReadOnlySpan<byte> parentSeparator = default)
    {
        // 12-byte fixed header minimum.
        if (data.Length - nodeStart < 12)
            return default;

        int pos = nodeStart;
        byte flags = data[pos];
        int keyCount = BinaryPrimitives.ReadUInt16LittleEndian(data[(pos + 1)..]);
        int keySize = BinaryPrimitives.ReadUInt16LittleEndian(data[(pos + 3)..]);
        int prefixLen = data[pos + 5];
        ReadOnlySpan<byte> bo = data.Slice(pos + 6, 6);
        ulong baseOffset = (ulong)bo[0]
                         | ((ulong)bo[1] << 8)
                         | ((ulong)bo[2] << 16)
                         | ((ulong)bo[3] << 24)
                         | ((ulong)bo[4] << 32)
                         | ((ulong)bo[5] << 40);
        pos += 12;

        // When prefixLen > 0 the prefix bytes ride in from the caller's parentSeparator.
        // An insufficient parentSeparator (typical of value-only enumerators) leaves
        // commonKeyPrefix empty — see the doc on this method for which APIs stay valid
        // in that mode.
        ReadOnlySpan<byte> commonKeyPrefix = prefixLen > 0 && parentSeparator.Length >= prefixLen
            ? parentSeparator[..prefixLen]
            : default;

        NodeMetadata metadata = new()
        {
            Flags = flags,
            KeyCount = keyCount,
            KeySize = keySize,
            BaseOffset = baseOffset
        };

        int keysStart = pos;
        int keySectionSize = metadata.KeySectionSize;
        int valuesStart = keysStart + keySectionSize;
        int valueSectionSize = metadata.ValueSectionSize;
        int totalSize = (valuesStart + valueSectionSize) - nodeStart;

        return new BTreeNodeReader(
            metadata,
            data.Slice(valuesStart, valueSectionSize),
            data.Slice(keysStart, keySectionSize),
            commonKeyPrefix,
            totalSize);
    }

    /// <summary>
    /// Raw stored slot at <paramref name="index"/>, zero-copy. Bytes are in storage order, which
    /// for Variable is the 2-byte prefix slot and for LE-stored Uniform is the byte-reversed
    /// form of the original key. Only meaningful as a comparison token in the stored encoding —
    /// external callers wanting lex-order key bytes use <see cref="GetFullKey"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadOnlySpan<byte> GetRawSlot(int index) => metadata.KeyType switch
    {
        // Variable: SoA layout, prefix slot is byte-reversed (LE-stored). Returning the raw
        // 2-byte slot follows the same convention as LE-stored Uniform — callers that need
        // the full key in lex order use GetFullKey with a destination buffer.
        0 => keys.Slice(index * 2, 2),
        1 => keys.Slice(index * metadata.KeySize, metadata.KeySize),
        _ => throw new InvalidDataException($"Unknown KeyType: {metadata.KeyType}")
    };

    /// <summary>
    /// Get the value at the given entry index (raw bytes, no BaseOffset adjustment).
    /// Values are always Uniform: fixed-width <see cref="NodeMetadata.ValueSize"/> bytes per entry.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<byte> GetValue(int index) =>
        values.Slice(index * metadata.ValueSize, metadata.ValueSize);

    /// <summary>
    /// Get the unsigned integer value at the given entry index with BaseOffset applied.
    /// Reads the entry's value slot (1..8 byte LE Uniform width given by
    /// <see cref="NodeMetadata.ValueSize"/>) as a ulong and adds <see cref="NodeMetadata.BaseOffset"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong GetUInt64Value(int index)
    {
        ReadOnlySpan<byte> raw = GetValue(index);
        return ReadUInt64LE(raw) + metadata.BaseOffset;
    }

    /// <summary>
    /// Read a 1..8 byte little-endian unsigned integer. Higher bytes are zero-extended.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong ReadUInt64LE(ReadOnlySpan<byte> src)
    {
        ulong v = 0;
        int len = src.Length;
        for (int i = 0; i < len; i++)
            v |= (ulong)src[i] << (i * 8);
        return v;
    }

    // ---- Variable KEY (SoA) helpers ----

    /// <summary>
    /// Load entry <paramref name="index"/>'s prefix slot as a u16 (LE). The slot stores the
    /// original 2-byte prefix byte-reversed, so the unsigned value returned has the same
    /// ordering as a lex compare on the original prefix bytes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort GetVariableKeyPrefixU16(ReadOnlySpan<byte> keys, int index) =>
        Unsafe.ReadUnaligned<ushort>(
            ref Unsafe.Add(ref MemoryMarshal.GetReference(keys), (nint)(index * 2)));

    /// <summary>
    /// Load entry <paramref name="index"/>'s offset slot. High 2 bits = lenTag (0..3),
    /// low 14 bits = tailOffset (relative to remainingkeys section start).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetVariableKeyOffsetSlot(ReadOnlySpan<byte> keys, int count, int index)
    {
        int offsetArrStart = count * 2;
        return BinaryPrimitives.ReadUInt16LittleEndian(keys[(offsetArrStart + index * 2)..]);
    }

    /// <summary>
    /// Resolve the tail bytes for entry <paramref name="index"/>. Tag &lt; 11 returns an
    /// empty span. For tag 11 the tail spans <c>[tailOffset, nextTailOffset)</c> with the
    /// sentinel for the last entry being <c>remainingkeys.Length</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<byte> GetVariableKeyTail(ReadOnlySpan<byte> keys, int count, int index)
    {
        int offsetArrStart = count * 2;
        int tailStart = count * 4;
        int slot = BinaryPrimitives.ReadUInt16LittleEndian(keys[(offsetArrStart + index * 2)..]);
        if ((slot >>> 14) != 0b11) return default;
        int tailOffset = slot & 0x3FFF;
        int tailEnd;
        if (index + 1 < count)
        {
            int nextSlot = BinaryPrimitives.ReadUInt16LittleEndian(keys[(offsetArrStart + (index + 1) * 2)..]);
            tailEnd = nextSlot & 0x3FFF;
        }
        else
        {
            tailEnd = keys.Length - tailStart;
        }
        return keys.Slice(tailStart + tailOffset, tailEnd - tailOffset);
    }

    /// <summary>
    /// Encode the search key into the byte-reversed u16 form used by Variable prefixArr slots.
    /// Zero-pads keys shorter than 2 bytes; the caller still has to apply the lenTag-aware
    /// tie-break on prefix-equal probes (length 0/1/2 ambiguities collapse onto the same u16).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort EncodeVariableSearchPrefix(ReadOnlySpan<byte> q)
    {
        if (q.Length >= 2)
            return BinaryPrimitives.ReverseEndianness(
                Unsafe.ReadUnaligned<ushort>(ref MemoryMarshal.GetReference(q)));
        return q.Length == 1 ? (ushort)(q[0] << 8) : (ushort)0;
    }

    /// <summary>
    /// Compare query <paramref name="q"/> against entry <paramref name="index"/> using the
    /// SoA Variable layout. Returns negative, zero, or positive matching <c>SequenceCompareTo</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CompareVariableEntry(ReadOnlySpan<byte> q, ushort searchPrefix, ReadOnlySpan<byte> keys, int count, int index)
    {
        ushort midPrefix = GetVariableKeyPrefixU16(keys, index);
        if (searchPrefix != midPrefix)
            return searchPrefix > midPrefix ? 1 : -1;

        int slot = GetVariableKeyOffsetSlot(keys, count, index);
        int tag = slot >>> 14;
        if (tag != 0b11)
        {
            // Stored key length = tag (0/1/2). Prefix u16 equality (with zero padding) collapses
            // to a length tie-break: q.Length - storedLen.
            return q.Length - tag;
        }

        // Stored key has tail (length ≥ 3). q < stored if q exhausts within the prefix.
        if (q.Length <= 2) return -1;

        int tailOffset = slot & 0x3FFF;
        int offsetArrStart = count * 2;
        int tailStart = count * 4;
        int tailEnd = index + 1 < count
            ? BinaryPrimitives.ReadUInt16LittleEndian(keys[(offsetArrStart + (index + 1) * 2)..]) & 0x3FFF
            : keys.Length - tailStart;
        ReadOnlySpan<byte> tail = keys.Slice(tailStart + tailOffset, tailEnd - tailOffset);
        return q[2..].SequenceCompareTo(tail);
    }

    /// <summary>
    /// Strip the common key prefix from <paramref name="key"/>. Returns the residual span
    /// to binary-search against suffixes, or signals via <paramref name="shortcutResult"/>
    /// that the answer is determined entirely by the prefix relationship.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryStripCommonPrefix(ReadOnlySpan<byte> key, out ReadOnlySpan<byte> residual, out int shortcutResult)
    {
        if (commonKeyPrefix.Length == 0)
        {
            residual = key;
            shortcutResult = 0;
            return true;
        }
        if (key.StartsWith(commonKeyPrefix))
        {
            residual = key[commonKeyPrefix.Length..];
            shortcutResult = 0;
            return true;
        }
        // key does not start with prefix — relationship to every stored key is fixed.
        residual = default;
        shortcutResult = key.SequenceCompareTo(commonKeyPrefix) < 0
            ? -1                       // key < prefix ≤ every stored key → no floor
            : metadata.KeyCount - 1;  // key > prefix && !StartsWith(prefix) → floor = last
        return false;
    }

    /// <summary>
    /// Find the index of the largest entry whose key is &lt;= searchKey.
    /// Returns -1 if key is less than all entries.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int FindFloorIndex(ReadOnlySpan<byte> key)
    {
        if (!TryStripCommonPrefix(key, out ReadOnlySpan<byte> q, out int shortcut))
            return shortcut;

        int count = metadata.KeyCount;
        if (count == 0) return -1;

        // q is the search key with CommonKeyPrefix stripped; keys holds the matching
        // stripped separators, so the lexicographic compare is consistent.
        bool keyLe = metadata.IsKeyLittleEndian;
        int keySize = metadata.KeySize;
        return metadata.KeyType switch
        {
            1 => keyLe
                ? keySize switch
                {
                    2 => UniformKeySearch.Uniform2LE(q, keys, count),
                    3 => UniformKeySearch.Uniform3LE(q, keys, count),
                    4 => UniformKeySearch.Uniform4LE(q, keys, count),
                    8 => UniformKeySearch.Uniform8LE(q, keys, count),
                    _ => throw new InvalidDataException($"Invalid LE keySize: {keySize}")
                }
                : UniformKeySearch.UniformBE(q, keys, count, keySize),
            0 => FindFloorIndexVariable(q, keys, count),
            _ => throw new InvalidDataException($"Unknown KeyType: {metadata.KeyType}")
        };
    }

    /// <summary>
    /// Find the largest entry whose key is &lt;= searchKey (floor lookup).
    /// Returns true and sets floorKey/floorValue if found. <paramref name="floorKey"/> is
    /// the per-entry suffix; the full stored key is <see cref="CommonKeyPrefix"/> followed
    /// by <paramref name="floorKey"/>.
    /// </summary>
    public bool TryGetFloor(ReadOnlySpan<byte> key, out ReadOnlySpan<byte> floorKey, out ReadOnlySpan<byte> floorValue)
    {
        // FindFloorIndex handles both the empty-node early-return and the
        // CommonKeyPrefix strip + KeyType dispatch.
        int result = FindFloorIndex(key);
        if (result < 0)
        {
            floorKey = default;
            floorValue = default;
            return false;
        }

        floorKey = GetRawSlot(result);
        floorValue = GetValue(result);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FindFloorIndexVariable(ReadOnlySpan<byte> key, ReadOnlySpan<byte> keys, int count)
    {
        ushort searchPrefix = EncodeVariableSearchPrefix(key);
        int result = -1;
        int lo = 0, hi = count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            int cmp = CompareVariableEntry(key, searchPrefix, keys, count, mid);
            if (cmp >= 0) { result = mid; lo = mid + 1; }
            else { hi = mid - 1; }
        }
        return result;
    }

    /// <summary>
    /// Copy the full key (common prefix + per-entry suffix) for entry <paramref name="index"/>
    /// into <paramref name="dest"/>. Always emits bytes in original (lex) order, byte-swapping
    /// the per-entry suffix when <see cref="NodeMetadata.IsKeyLittleEndian"/> is set.
    /// Returns the total number of bytes written.
    /// </summary>
    public int GetFullKey(int index, Span<byte> dest)
    {
        if (metadata.KeyType == 0)
        {
            // Variable: prefix slot is byte-reversed; tail (if tag 11) lives in remainingkeys.
            int slot = GetVariableKeyOffsetSlot(keys, metadata.KeyCount, index);
            int tag = slot >>> 14;
            ReadOnlySpan<byte> tail = tag == 0b11
                ? GetVariableKeyTail(keys, metadata.KeyCount, index)
                : default;
            int suffixLen = tag == 0b11 ? 2 + tail.Length : tag;
            int total = commonKeyPrefix.Length + suffixLen;
            if (dest.Length < total)
                throw new ArgumentException("Destination too small for full key", nameof(dest));
            commonKeyPrefix.CopyTo(dest);
            Span<byte> suffixDst = dest.Slice(commonKeyPrefix.Length, suffixLen);
            // Un-reverse prefix slot bytes [b, a] → lex [a, b] up to suffixLen.
            if (suffixLen >= 1) suffixDst[0] = keys[index * 2 + 1];
            if (suffixLen >= 2) suffixDst[1] = keys[index * 2];
            if (tag == 0b11) tail.CopyTo(suffixDst[2..]);
            return total;
        }

        ReadOnlySpan<byte> suffix = GetRawSlot(index);
        int totalLegacy = commonKeyPrefix.Length + suffix.Length;
        if (dest.Length < totalLegacy)
            throw new ArgumentException("Destination too small for full key", nameof(dest));
        commonKeyPrefix.CopyTo(dest);
        Span<byte> suffixDstLegacy = dest.Slice(commonKeyPrefix.Length, suffix.Length);
        if (metadata.IsKeyLittleEndian)
        {
            // Stored slots for KeyType ∈ {1,2} with LE flag are byte-reversed on disk.
            // Reverse back into dest to recover the original lex/numeric byte order.
            int n = suffix.Length;
            for (int i = 0; i < n; i++) suffixDstLegacy[i] = suffix[n - 1 - i];
        }
        else
        {
            suffix.CopyTo(suffixDstLegacy);
        }
        return totalLegacy;
    }

    /// <summary>
    /// Copy entry <paramref name="index"/>'s full lex-order separator bytes (common prefix +
    /// per-entry suffix) into <paramref name="dest"/>. Returns the number of bytes written.
    /// Equivalent to <see cref="GetFullKey"/> — callers descending into a child node use this
    /// to materialize the bytes that the child's header omits.
    /// </summary>
    public int GetSeparatorBytes(int index, Span<byte> dest) => GetFullKey(index, dest);

    /// <summary>
    /// Enumerate all key-value pairs in order.
    /// </summary>
    public Enumerator GetEnumerator() => new(this);

    public ref struct Enumerator
    {
        private readonly BTreeNodeReader _index;
        private int _current;

        public Enumerator(BTreeNodeReader index)
        {
            _index = index;
            _current = -1;
        }

        public bool MoveNext() => ++_current < _index.EntryCount;

        public readonly IndexEntry Current => new(_index.GetRawSlot(_current), _index.GetValue(_current));
    }

    public readonly ref struct IndexEntry(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        public ReadOnlySpan<byte> Key { get; } = key;
        public ReadOnlySpan<byte> Value { get; } = value;
    }

}
