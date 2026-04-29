// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Utils;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Hsst;
using Nethermind.Trie;

namespace Nethermind.State.Flat.PersistedSnapshots;

/// <summary>
/// Static decoding/reading methods and enumerators for persisted snapshot data.
/// All methods operate on raw <see cref="ReadOnlySpan{T}"/> HSST data.
/// </summary>
public static class PersistedSnapshotReader
{
    private const int TopPathThreshold = 5;
    private const int CompactPathThreshold = 15;
    private const int StorageHashPrefixLength = 20;
    private const int SlotPrefixLength = 30;

    internal static bool TryGetAccount(ReadOnlySpan<byte> data, Address address, [UnscopedRef] out ReadOnlySpan<byte> accountRlp)
    {
        SpanByteReader reader = new(data);
        using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
        if (!r.TrySeek(PersistedSnapshot.AccountColumnTag, out _) ||
            !r.TrySeek(address.Bytes, out _) ||
            !r.TrySeek(PersistedSnapshot.AccountSubTag, out _))
        {
            accountRlp = default;
            return false;
        }
        accountRlp = SliceFromBound(data, r.GetBound());
        return true;
    }

    internal static bool TryGetSlot(ReadOnlySpan<byte> data, Address address, in UInt256 index, [UnscopedRef] out ReadOnlySpan<byte> slotValue)
    {
        SpanByteReader reader = new(data);
        using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
        Span<byte> slotKey = stackalloc byte[32];
        index.ToBigEndian(slotKey);
        if (!r.TrySeek(PersistedSnapshot.AccountColumnTag, out _) ||
            !r.TrySeek(address.Bytes, out _) ||
            !r.TrySeek(PersistedSnapshot.SlotSubTag, out _) ||
            !r.TrySeek(slotKey[..SlotPrefixLength], out _) ||
            !r.TrySeek(slotKey[SlotPrefixLength..], out _))
        {
            slotValue = default;
            return false;
        }
        slotValue = SliceFromBound(data, r.GetBound());
        return true;
    }

    internal static bool IsSelfDestructed(ReadOnlySpan<byte> data, Address address)
    {
        SpanByteReader reader = new(data);
        using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
        return r.TrySeek(PersistedSnapshot.AccountColumnTag, out _)
            && r.TrySeek(address.Bytes, out _)
            && r.TrySeek(PersistedSnapshot.SelfDestructSubTag, out _);
    }

    internal static bool? TryGetSelfDestructFlag(ReadOnlySpan<byte> data, Address address)
    {
        SpanByteReader reader = new(data);
        using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
        if (!r.TrySeek(PersistedSnapshot.AccountColumnTag, out _) ||
            !r.TrySeek(address.Bytes, out _) ||
            !r.TrySeek(PersistedSnapshot.SelfDestructSubTag, out _))
            return null;
        Bound b = r.GetBound();
        return b.Length > 0 && data[(int)b.Offset] == 0x01;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<byte> SliceFromBound(ReadOnlySpan<byte> data, Bound b) =>
        data.Slice((int)b.Offset, b.Length);

    internal static bool TryLoadStateNodeRlp(ReadOnlySpan<byte> data, scoped in TreePath path,
        Dictionary<int, PersistedSnapshot>? referencedSnapshots, bool hasNodeRefs, out ReadOnlySpan<byte> nodeRlp)
    {
        if (path.Length <= TopPathThreshold)
        {
            Span<byte> key = stackalloc byte[3];
            path.EncodeWith3Byte(key);
            if (!TryGetFromColumn(data, PersistedSnapshot.StateTopNodesTag, key, out nodeRlp)) return false;
            TryResolveNodeRef(nodeRlp, out nodeRlp, referencedSnapshots, hasNodeRefs);
            return true;
        }
        if (path.Length <= CompactPathThreshold)
        {
            Span<byte> key = stackalloc byte[8];
            path.EncodeWith8Byte(key);
            if (!TryGetFromColumn(data, PersistedSnapshot.StateNodeTag, key, out nodeRlp)) return false;
            TryResolveNodeRef(nodeRlp, out nodeRlp, referencedSnapshots, hasNodeRefs);
            return true;
        }
        Span<byte> fullKey = stackalloc byte[33];
        path.Path.Bytes.CopyTo(fullKey);
        fullKey[32] = (byte)path.Length;
        if (!TryGetFromColumn(data, PersistedSnapshot.StateNodeFallbackTag, fullKey, out nodeRlp)) return false;
        TryResolveNodeRef(nodeRlp, out nodeRlp, referencedSnapshots, hasNodeRefs);
        return true;
    }

    internal static bool TryLoadStorageNodeRlp(ReadOnlySpan<byte> data, Hash256 address, in TreePath path,
        Dictionary<int, PersistedSnapshot>? referencedSnapshots, bool hasNodeRefs, scoped out ReadOnlySpan<byte> nodeRlp)
    {
        if (path.Length <= CompactPathThreshold)
        {
            Span<byte> key = stackalloc byte[8];
            path.EncodeWith8Byte(key);
            if (!TryGetNestedValue(data, PersistedSnapshot.StorageNodeTag, address.Bytes[..StorageHashPrefixLength], key, out nodeRlp)) return false;
            TryResolveNodeRef(nodeRlp, out nodeRlp, referencedSnapshots, hasNodeRefs);
            return true;
        }
        Span<byte> fullKey = stackalloc byte[33];
        path.Path.Bytes.CopyTo(fullKey);
        fullKey[32] = (byte)path.Length;
        if (!TryGetNestedValue(data, PersistedSnapshot.StorageNodeFallbackTag, address.Bytes[..StorageHashPrefixLength], fullKey, out nodeRlp)) return false;
        TryResolveNodeRef(nodeRlp, out nodeRlp, referencedSnapshots, hasNodeRefs);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void TryResolveNodeRef(ReadOnlySpan<byte> value, out ReadOnlySpan<byte> resolved,
        Dictionary<int, PersistedSnapshot>? referencedSnapshots, bool hasNodeRefs)
    {
        if (!hasNodeRefs || referencedSnapshots is null)
        {
            resolved = value;
            return;
        }

        NodeRef nodeRef = NodeRef.Read(value);
        if (!referencedSnapshots.TryGetValue(nodeRef.SnapshotId, out PersistedSnapshot? snapshot))
            throw new InvalidOperationException($"Referenced snapshot {nodeRef.SnapshotId} not found");
        resolved = DecodeValueAt(snapshot.GetSpan(), nodeRef.ValueLengthOffset);
    }

    internal static bool CheckHasNodeRefsFlag(ReadOnlySpan<byte> data)
    {
        SpanByteReader reader = new(data);
        using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
        return r.TrySeek(PersistedSnapshot.MetadataTag, out _)
            && r.TrySeek("noderefs"u8, out _);
    }

    internal static int[]? ReadRefIdsFromMetadata(ReadOnlySpan<byte> snapshotData)
    {
        SpanByteReader reader = new(snapshotData);
        using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
        if (!r.TrySeek(PersistedSnapshot.MetadataTag, out _) ||
            !r.TrySeek("ref_ids"u8, out _))
            return null;
        Bound b = r.GetBound();
        if (b.Length == 0 || b.Length % 4 != 0) return null;
        ReadOnlySpan<byte> refIdBytes = SliceFromBound(snapshotData, b);
        int count = refIdBytes.Length / 4;
        int[] ids = new int[count];
        for (int i = 0; i < count; i++)
            ids[i] = BitConverter.ToInt32(refIdBytes.Slice(i * 4, 4));
        return ids;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static byte[] ResolveValue(ReadOnlySpan<byte> snapshotData, int valueLengthOffset) =>
        DecodeValueAt(snapshotData, valueLengthOffset).ToArray();

    /// <summary>
    /// Decode the value bytes for a non-inline HSST entry whose metadata starts at
    /// <paramref name="metadataStart"/>. Entry layout: <c>[Value][ValueLength: LEB128][...]</c>.
    /// Reads the LEB128 forward, then the value lives in the <paramref name="valueLength"/>
    /// bytes immediately preceding <paramref name="metadataStart"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<byte> DecodeValueAt(ReadOnlySpan<byte> data, int metadataStart)
    {
        int pos = metadataStart;
        int valueLength = Leb128.Read(data, ref pos);
        return data.Slice(metadataStart - valueLength, valueLength);
    }

    private static bool TryGetFromColumn(ReadOnlySpan<byte> data, scoped ReadOnlySpan<byte> tag, scoped ReadOnlySpan<byte> entityKey, scoped out ReadOnlySpan<byte> value)
    {
        SpanByteReader reader = new(data);
        using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
        if (!r.TrySeek(tag, out _) || !r.TrySeek(entityKey, out _))
        {
            value = default;
            return false;
        }
        value = SliceFromBound(data, r.GetBound());
        return true;
    }

    private static bool TryGetNestedValue(ReadOnlySpan<byte> data, scoped ReadOnlySpan<byte> tag, scoped ReadOnlySpan<byte> addressKey, scoped ReadOnlySpan<byte> entityKey, out ReadOnlySpan<byte> value)
    {
        SpanByteReader reader = new(data);
        using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
        if (!r.TrySeek(tag, out _) || !r.TrySeek(addressKey, out _) || !r.TrySeek(entityKey, out _))
        {
            value = default;
            return false;
        }
        value = SliceFromBound(data, r.GetBound());
        return true;
    }

    private static bool TryGetDoubleNestedValue(
        ReadOnlySpan<byte> data,
        scoped ReadOnlySpan<byte> tag,
        scoped ReadOnlySpan<byte> addressKey,
        scoped ReadOnlySpan<byte> prefixKey,
        scoped ReadOnlySpan<byte> suffixKey,
        out ReadOnlySpan<byte> value)
    {
        SpanByteReader reader = new(data);
        using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
        if (!r.TrySeek(tag, out _) ||
            !r.TrySeek(addressKey, out _) ||
            !r.TrySeek(prefixKey, out _) ||
            !r.TrySeek(suffixKey, out _))
        {
            value = default;
            return false;
        }
        value = SliceFromBound(data, r.GetBound());
        return true;
    }

    internal static TreePath DecodeCompactTreePath(ReadOnlySpan<byte> key) =>
        TreePath.DecodeWith8Byte(key);

    internal static Hash256 DecodeAddressHash(ReadOnlySpan<byte> key)
    {
        Span<byte> padded = stackalloc byte[32];
        key.CopyTo(padded);
        return new Hash256(padded);
    }

    // --- Enumerables and enumerators ---

    public readonly ref struct SelfDestructEnumerable(PersistedSnapshot snapshot)
    {
        private readonly PersistedSnapshot _snapshot = snapshot;
        public readonly SelfDestructEnumerator GetEnumerator() => new(_snapshot);
    }

    public ref struct SelfDestructEnumerator : IDisposable
    {
        private readonly KeyValuePair<AddressAsKey, bool>[] _entries;
        private int _index;

        public SelfDestructEnumerator(PersistedSnapshot snapshot)
        {
            _index = -1;
            ReadOnlySpan<byte> snapshotData = snapshot.GetSpan();
            SpanByteReader reader = new(snapshotData);
            HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
            if (!r.TrySeek(PersistedSnapshot.AccountColumnTag, out _))
            {
                _entries = [];
                return;
            }

            List<KeyValuePair<AddressAsKey, bool>> list = [];
            using HsstEnumerator<SpanByteReader, NoOpPin> addrEnum = new(in reader, r.GetBound());
            while (addrEnum.MoveNext())
            {
                KeyValueEntry addrEntry = addrEnum.Current;
                HsstReader<SpanByteReader, NoOpPin> perAddr = new(in reader, addrEntry.ValueBound);
                if (perAddr.TrySeek(PersistedSnapshot.SelfDestructSubTag, out _))
                {
                    Bound sdBound = perAddr.GetBound();
                    Address addr = new(SliceFromBound(snapshotData, addrEntry.KeyBound).ToArray());
                    bool isNew = sdBound.Length > 0 && snapshotData[(int)sdBound.Offset] == 0x01;
                    list.Add(new(addr, isNew));
                }
            }

            _entries = [.. list];
        }

        public bool MoveNext() => ++_index < _entries.Length;
        public readonly KeyValuePair<AddressAsKey, bool> Current => _entries[_index];
        public readonly void Dispose() { }
    }

    public readonly ref struct AccountEnumerable(PersistedSnapshot snapshot)
    {
        private readonly PersistedSnapshot _snapshot = snapshot;
        public readonly AccountEnumerator GetEnumerator() => new(_snapshot);
    }

    public ref struct AccountEnumerator : IDisposable
    {
        private readonly KeyValuePair<AddressAsKey, Account?>[] _entries;
        private int _index;

        public AccountEnumerator(PersistedSnapshot snapshot)
        {
            _index = -1;
            ReadOnlySpan<byte> snapshotData = snapshot.GetSpan();
            SpanByteReader reader = new(snapshotData);
            HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
            if (!r.TrySeek(PersistedSnapshot.AccountColumnTag, out _))
            {
                _entries = [];
                return;
            }

            List<KeyValuePair<AddressAsKey, Account?>> list = [];
            using HsstEnumerator<SpanByteReader, NoOpPin> addrEnum = new(in reader, r.GetBound());
            while (addrEnum.MoveNext())
            {
                KeyValueEntry addrEntry = addrEnum.Current;
                HsstReader<SpanByteReader, NoOpPin> perAddr = new(in reader, addrEntry.ValueBound);
                if (perAddr.TrySeek(PersistedSnapshot.AccountSubTag, out _))
                {
                    Bound rlpBound = perAddr.GetBound();
                    Address addr = new(SliceFromBound(snapshotData, addrEntry.KeyBound).ToArray());
                    ReadOnlySpan<byte> accountRlp = SliceFromBound(snapshotData, rlpBound);
                    Account? account = accountRlp.IsEmpty
                        ? null
                        : AccountDecoder.Slim.Decode(accountRlp);
                    list.Add(new(addr, account));
                }
            }

            _entries = [.. list];
        }

        public bool MoveNext() => ++_index < _entries.Length;
        public readonly KeyValuePair<AddressAsKey, Account?> Current => _entries[_index];
        public readonly void Dispose() { }
    }

    public readonly ref struct StorageEnumerable(PersistedSnapshot snapshot)
    {
        private readonly PersistedSnapshot _snapshot = snapshot;
        public readonly StorageEnumerator GetEnumerator() => new(_snapshot);
    }

    public ref struct StorageEnumerator : IDisposable
    {
        private readonly KeyValuePair<(AddressAsKey, UInt256), SlotValue?>[] _entries;
        private int _index;

        public StorageEnumerator(PersistedSnapshot snapshot)
        {
            _index = -1;
            ReadOnlySpan<byte> snapshotData = snapshot.GetSpan();
            SpanByteReader reader = new(snapshotData);
            HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
            if (!r.TrySeek(PersistedSnapshot.AccountColumnTag, out _))
            {
                _entries = [];
                return;
            }

            List<KeyValuePair<(AddressAsKey, UInt256), SlotValue?>> list = [];
            using HsstEnumerator<SpanByteReader, NoOpPin> addrEnum = new(in reader, r.GetBound());
            while (addrEnum.MoveNext())
            {
                KeyValueEntry addrEntry = addrEnum.Current;
                HsstReader<SpanByteReader, NoOpPin> perAddr = new(in reader, addrEntry.ValueBound);
                if (!perAddr.TrySeek(PersistedSnapshot.SlotSubTag, out _))
                    continue;

                Address addr = new(SliceFromBound(snapshotData, addrEntry.KeyBound).ToArray());
                Bound slotBound = perAddr.GetBound();
                using HsstEnumerator<SpanByteReader, NoOpPin> prefixEnum = new(in reader, slotBound);
                while (prefixEnum.MoveNext())
                {
                    KeyValueEntry prefixEntry = prefixEnum.Current;
                    byte[] prefixBytes = SliceFromBound(snapshotData, prefixEntry.KeyBound).ToArray();
                    using HsstEnumerator<SpanByteReader, NoOpPin> suffixEnum = new(in reader, prefixEntry.ValueBound);
                    while (suffixEnum.MoveNext())
                    {
                        KeyValueEntry suffixEntry = suffixEnum.Current;
                        byte[] slotKey = new byte[32];
                        prefixBytes.CopyTo(slotKey.AsSpan());
                        SliceFromBound(snapshotData, suffixEntry.KeyBound).CopyTo(slotKey.AsSpan(SlotPrefixLength));
                        UInt256 slot = new(slotKey, isBigEndian: true);
                        ReadOnlySpan<byte> suffixValue = SliceFromBound(snapshotData, suffixEntry.ValueBound);
                        SlotValue? value = suffixValue.IsEmpty
                            ? null
                            : SlotValue.FromSpanWithoutLeadingZero(suffixValue);
                        list.Add(new((addr, slot), value));
                    }
                }
            }

            _entries = [.. list];
        }

        public bool MoveNext() => ++_index < _entries.Length;
        public readonly KeyValuePair<(AddressAsKey, UInt256), SlotValue?> Current => _entries[_index];
        public readonly void Dispose() { }
    }

    public readonly struct StateNodeEnumerable(PersistedSnapshot snapshot)
    {
        private readonly PersistedSnapshot _snapshot = snapshot;
        public StateNodeEnumerator GetEnumerator() => new(_snapshot);
    }

    public ref struct StateNodeEnumerator : IDisposable
    {
        private readonly KeyValuePair<TreePath, TrieNode>[] _entries;
        private int _index;

        public StateNodeEnumerator(PersistedSnapshot snapshot)
        {
            _index = -1;
            ReadOnlySpan<byte> snapshotData = snapshot.GetSpan();
            SpanByteReader reader = new(snapshotData);
            List<KeyValuePair<TreePath, TrieNode>> list = [];

            // Column 0x05: TopNodes (path length 0-5)
            {
                HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
                if (r.TrySeek(PersistedSnapshot.StateTopNodesTag, out _))
                {
                    using HsstEnumerator<SpanByteReader, NoOpPin> e = new(in reader, r.GetBound());
                    while (e.MoveNext())
                    {
                        KeyValueEntry entry = e.Current;
                        TreePath path = TreePath.DecodeWith3Byte(SliceFromBound(snapshotData, entry.KeyBound));
                        ReadOnlySpan<byte> rawValue = SliceFromBound(snapshotData, entry.ValueBound);
                        TryResolveNodeRef(rawValue, out ReadOnlySpan<byte> resolved,
                            snapshot.ReferencedSnapshotsLookup, snapshot.HasNodeRefs);
                        list.Add(new(path, new TrieNode(NodeType.Unknown, resolved.ToArray())));
                    }
                }
            }

            // Column 0x03: CompactNodes (path length 6-15)
            {
                HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
                if (r.TrySeek(PersistedSnapshot.StateNodeTag, out _))
                {
                    using HsstEnumerator<SpanByteReader, NoOpPin> e = new(in reader, r.GetBound());
                    while (e.MoveNext())
                    {
                        KeyValueEntry entry = e.Current;
                        TreePath path = DecodeCompactTreePath(SliceFromBound(snapshotData, entry.KeyBound));
                        ReadOnlySpan<byte> rawValue = SliceFromBound(snapshotData, entry.ValueBound);
                        TryResolveNodeRef(rawValue, out ReadOnlySpan<byte> resolved,
                            snapshot.ReferencedSnapshotsLookup, snapshot.HasNodeRefs);
                        list.Add(new(path, new TrieNode(NodeType.Unknown, resolved.ToArray())));
                    }
                }
            }

            // Column 0x06: Fallbacks (path length 16+)
            {
                HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
                if (r.TrySeek(PersistedSnapshot.StateNodeFallbackTag, out _))
                {
                    using HsstEnumerator<SpanByteReader, NoOpPin> e = new(in reader, r.GetBound());
                    while (e.MoveNext())
                    {
                        KeyValueEntry entry = e.Current;
                        ReadOnlySpan<byte> entryKey = SliceFromBound(snapshotData, entry.KeyBound);
                        TreePath path = new(new ValueHash256(entryKey[..32]), entryKey[32]);
                        ReadOnlySpan<byte> rawValue = SliceFromBound(snapshotData, entry.ValueBound);
                        TryResolveNodeRef(rawValue, out ReadOnlySpan<byte> resolved,
                            snapshot.ReferencedSnapshotsLookup, snapshot.HasNodeRefs);
                        list.Add(new(path, new TrieNode(NodeType.Unknown, resolved.ToArray())));
                    }
                }
            }

            _entries = [.. list];
        }

        public bool MoveNext() => ++_index < _entries.Length;
        public readonly KeyValuePair<TreePath, TrieNode> Current => _entries[_index];
        public readonly void Dispose() { }
    }

    public readonly struct StorageNodeEnumerable(PersistedSnapshot snapshot)
    {
        private readonly PersistedSnapshot _snapshot = snapshot;
        public StorageNodeEnumerator GetEnumerator() => new(_snapshot);
    }

    public ref struct StorageNodeEnumerator : IDisposable
    {
        private readonly KeyValuePair<(Hash256AsKey, TreePath), TrieNode>[] _entries;
        private int _index;

        public StorageNodeEnumerator(PersistedSnapshot snapshot)
        {
            _index = -1;
            ReadOnlySpan<byte> snapshotData = snapshot.GetSpan();
            SpanByteReader reader = new(snapshotData);
            List<KeyValuePair<(Hash256AsKey, TreePath), TrieNode>> list = [];

            // Column 0x07: StorageNode (path ≤15, compact 8-byte key)
            {
                HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
                if (r.TrySeek(PersistedSnapshot.StorageNodeTag, out _))
                {
                    using HsstEnumerator<SpanByteReader, NoOpPin> hashEnum = new(in reader, r.GetBound());
                    while (hashEnum.MoveNext())
                    {
                        KeyValueEntry hashEntry = hashEnum.Current;
                        Hash256 addressHash = DecodeAddressHash(SliceFromBound(snapshotData, hashEntry.KeyBound));
                        using HsstEnumerator<SpanByteReader, NoOpPin> pathEnum = new(in reader, hashEntry.ValueBound);
                        while (pathEnum.MoveNext())
                        {
                            KeyValueEntry pathEntry = pathEnum.Current;
                            TreePath path = DecodeCompactTreePath(SliceFromBound(snapshotData, pathEntry.KeyBound));
                            ReadOnlySpan<byte> rawValue = SliceFromBound(snapshotData, pathEntry.ValueBound);
                            TryResolveNodeRef(rawValue, out ReadOnlySpan<byte> resolved,
                                snapshot.ReferencedSnapshotsLookup, snapshot.HasNodeRefs);
                            list.Add(new((addressHash, path), new TrieNode(NodeType.Unknown, resolved.ToArray())));
                        }
                    }
                }
            }

            // Column 0x08: StorageNodeFallback (path ≥16, 33-byte key)
            {
                HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
                if (r.TrySeek(PersistedSnapshot.StorageNodeFallbackTag, out _))
                {
                    using HsstEnumerator<SpanByteReader, NoOpPin> hashEnum = new(in reader, r.GetBound());
                    while (hashEnum.MoveNext())
                    {
                        KeyValueEntry hashEntry = hashEnum.Current;
                        Hash256 addressHash = DecodeAddressHash(SliceFromBound(snapshotData, hashEntry.KeyBound));
                        using HsstEnumerator<SpanByteReader, NoOpPin> pathEnum = new(in reader, hashEntry.ValueBound);
                        while (pathEnum.MoveNext())
                        {
                            KeyValueEntry pathEntry = pathEnum.Current;
                            ReadOnlySpan<byte> pathKey = SliceFromBound(snapshotData, pathEntry.KeyBound);
                            TreePath path = new(new ValueHash256(pathKey[..32]), pathKey[32]);
                            ReadOnlySpan<byte> rawValue = SliceFromBound(snapshotData, pathEntry.ValueBound);
                            TryResolveNodeRef(rawValue, out ReadOnlySpan<byte> resolved,
                                snapshot.ReferencedSnapshotsLookup, snapshot.HasNodeRefs);
                            list.Add(new((addressHash, path), new TrieNode(NodeType.Unknown, resolved.ToArray())));
                        }
                    }
                }
            }

            _entries = [.. list];
        }

        public bool MoveNext() => ++_index < _entries.Length;
        public readonly KeyValuePair<(Hash256AsKey, TreePath), TrieNode> Current => _entries[_index];
        public readonly void Dispose() { }
    }
}
