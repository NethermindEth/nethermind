// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Nethermind.State.Flat.Hsst.BTree;

/// <summary>
/// Reads a B-tree index block: a fixed-width metadata header followed by the keys and
/// values sections, parsed forward from the node's start offset.
/// </summary>
/// <remarks>
/// Node wire layout (header, Flags bits, KeyType, value-slot widths, Variable-key SoA
/// section): see <c>Hsst/FORMAT.md</c>, "B-tree index node layout" and "Keys section
/// (Variable)".
/// <para>
/// When <c>CommonPrefixLen &gt; 0</c> the keys section holds suffixes only; the prefix
/// bytes are supplied by the caller via <see cref="ReadFromStart"/>'s <c>parentSeparator</c>
/// (the parent's matched separator, or the HSST trailer for the root). Use
/// <see cref="GetSeparatorBytes"/> to reconstruct lex bytes.
/// </para>
/// </remarks>
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
    /// <see cref="GetSeparatorBytes"/>.
    /// </summary>
    public ReadOnlySpan<byte> CommonKeyPrefix => commonKeyPrefix;

    /// <summary>
    /// Read an index block forward from <paramref name="nodeStart"/> (inclusive start position).
    /// <paramref name="parentSeparator"/> supplies the common-key-prefix bytes for nodes whose
    /// header records a non-zero <c>CommonPrefixLen</c>. Must be the full lex-order separator
    /// bytes the parent used to route into this node — the builder guarantees
    /// <c>parentSeparator.Length &gt;= CommonPrefixLen</c>. Pass <c>default</c> when the caller
    /// only needs value-only access (e.g. <see cref="HsstEnumerator{TReader,TPin}"/>): the
    /// prefix-dependent paths (<see cref="TryGetFloor"/>, <see cref="GetSeparatorBytes"/>) will
    /// misbehave but <see cref="GetUInt64Value"/>, <see cref="EntryCount"/>, and friends still work.
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
        // 6-byte LE base offset read as u32 (bytes 0-3) | u16 (bytes 4-5) << 32. Reads exactly the
        // 6 header bytes; a single ReadUInt64 would over-read past a minimal 12-byte node.
        ulong baseOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(pos + 6, 4))
                         | ((ulong)BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(pos + 10, 2)) << 32);
        pos += 12;

        // When prefixLen > 0 the prefix bytes ride in from the caller's parentSeparator.
        // A value-only caller passes an empty parentSeparator (see the method doc) and gets an
        // empty commonKeyPrefix — the prefix-dependent APIs are documented to misbehave then. A
        // non-empty but too-short separator is a contract violation: the builder guarantees
        // parentSeparator.Length >= CommonPrefixLen for every real descent.
        ReadOnlySpan<byte> commonKeyPrefix;
        if (prefixLen == 0 || parentSeparator.Length == 0)
            commonKeyPrefix = default;
        else if (parentSeparator.Length >= prefixLen)
            commonKeyPrefix = parentSeparator[..prefixLen];
        else
            throw new InvalidDataException(
                $"parentSeparator length {parentSeparator.Length} is shorter than the node's CommonPrefixLen {prefixLen}.");

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
    /// external callers wanting lex-order key bytes use <see cref="GetSeparatorBytes"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadOnlySpan<byte> GetRawSlot(int index) => metadata.KeyType switch
    {
        0 => new BTreeNodeVariableKeyReader(keys, metadata.KeyCount).GetRawSlot(index),
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
        // Full-width slot: a single LE load. Partial widths (1..7) fall back to a byte loop —
        // padding up to 8 would need a stackalloc (disqualifies this hot helper from inlining)
        // and over-reading src would overrun the last value slot.
        if (src.Length == 8) return BinaryPrimitives.ReadUInt64LittleEndian(src);
        ulong v = 0;
        for (int i = 0; i < src.Length; i++)
            v |= (ulong)src[i] << (i * 8);
        return v;
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
    internal int FindFloorIndex(ReadOnlySpan<byte> key)
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
                    4 => UniformKeySearch.Uniform4LE(q, keys, count),
                    8 => UniformKeySearch.Uniform8LE(q, keys, count),
                    _ => throw new InvalidDataException($"Invalid LE keySize: {keySize}")
                }
                : UniformKeySearch.UniformBE(q, keys, count, keySize),
            0 => new BTreeNodeVariableKeyReader(keys, count).FindFloorIndex(q),
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

    /// <summary>
    /// Copy entry <paramref name="index"/>'s full routing separator (common prefix + per-entry
    /// suffix) into <paramref name="dest"/>. Always emits bytes in original (lex) order,
    /// byte-swapping the per-entry suffix when <see cref="NodeMetadata.IsKeyLittleEndian"/> is set.
    /// Returns the total number of bytes written.
    /// </summary>
    /// <remarks>
    /// Used when descending into a child: the child's header omits its common-prefix bytes, so the
    /// parent materializes the matched separator here and passes it as the next
    /// <see cref="ReadFromStart"/>'s <c>parentSeparator</c>.
    /// </remarks>
    internal int GetSeparatorBytes(int index, Span<byte> dest)
    {
        if (metadata.KeyType == 0)
            return new BTreeNodeVariableKeyReader(keys, metadata.KeyCount).GetSeparatorBytes(index, commonKeyPrefix, dest);

        ReadOnlySpan<byte> suffix = GetRawSlot(index);
        int total = commonKeyPrefix.Length + suffix.Length;
        if (dest.Length < total)
            throw new ArgumentException("Destination too small for full key", nameof(dest));
        commonKeyPrefix.CopyTo(dest);
        Span<byte> suffixDst = dest.Slice(commonKeyPrefix.Length, suffix.Length);
        if (metadata.IsKeyLittleEndian)
        {
            // Stored slots for KeyType ∈ {1,2} with LE flag are byte-reversed on disk.
            // Reverse back into dest to recover the original lex/numeric byte order.
            int n = suffix.Length;
            for (int i = 0; i < n; i++) suffixDst[i] = suffix[n - 1 - i];
        }
        else
        {
            suffix.CopyTo(suffixDst);
        }
        return total;
    }
}
