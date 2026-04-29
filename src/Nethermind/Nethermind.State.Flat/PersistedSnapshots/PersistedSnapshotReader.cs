// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
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
        Hsst.Hsst.ReadEntry(snapshot.GetSpan(), nodeRef.ValueLengthOffset, out _, out resolved);
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
    internal static byte[] ResolveValue(ReadOnlySpan<byte> snapshotData, int valueLengthOffset)
    {
        Hsst.Hsst.ReadEntry(snapshotData, valueLengthOffset, out _, out ReadOnlySpan<byte> value);
        return value.ToArray();
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
            Hsst.Hsst outer = new(snapshotData);
            if (!outer.TryGet(PersistedSnapshot.AccountColumnTag, out ReadOnlySpan<byte> column))
            {
                _entries = [];
                return;
            }

            List<KeyValuePair<AddressAsKey, bool>> list = [];
            Hsst.IHsstReadahead? readahead = snapshot.CreateColumnReadahead(PersistedSnapshot.AccountColumnTag);
            Hsst.Hsst addressLevel = new(column);
            using Hsst.Hsst.Enumerator addrEnum = addressLevel.GetEnumerator(readahead);
            while (addrEnum.MoveNext())
            {
                Hsst.Hsst.KeyValueEntry addrEntry = addrEnum.Current;
                Hsst.Hsst perAddr = new(addrEntry.Value);
                if (perAddr.TryGet(PersistedSnapshot.SelfDestructSubTag, out ReadOnlySpan<byte> sdValue))
                {
                    Address addr = new(addrEntry.Key.ToArray());
                    bool isNew = !sdValue.IsEmpty && sdValue[0] == 0x01;
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
            Hsst.Hsst outer = new(snapshotData);
            if (!outer.TryGet(PersistedSnapshot.AccountColumnTag, out ReadOnlySpan<byte> column))
            {
                _entries = [];
                return;
            }

            List<KeyValuePair<AddressAsKey, Account?>> list = [];
            Hsst.IHsstReadahead? readahead = snapshot.CreateColumnReadahead(PersistedSnapshot.AccountColumnTag);
            Hsst.Hsst addressLevel = new(column);
            using Hsst.Hsst.Enumerator addrEnum = addressLevel.GetEnumerator(readahead);
            while (addrEnum.MoveNext())
            {
                Hsst.Hsst.KeyValueEntry addrEntry = addrEnum.Current;
                Hsst.Hsst perAddr = new(addrEntry.Value);
                if (perAddr.TryGet(PersistedSnapshot.AccountSubTag, out ReadOnlySpan<byte> accountRlp))
                {
                    Address addr = new(addrEntry.Key.ToArray());
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
            Hsst.Hsst outer = new(snapshotData);
            if (!outer.TryGet(PersistedSnapshot.AccountColumnTag, out ReadOnlySpan<byte> column))
            {
                _entries = [];
                return;
            }

            List<KeyValuePair<(AddressAsKey, UInt256), SlotValue?>> list = [];
            Hsst.IHsstReadahead? readahead = snapshot.CreateColumnReadahead(PersistedSnapshot.AccountColumnTag);
            Hsst.Hsst addressLevel = new(column);
            using Hsst.Hsst.Enumerator addrEnum = addressLevel.GetEnumerator(readahead);
            while (addrEnum.MoveNext())
            {
                Hsst.Hsst.KeyValueEntry addrEntry = addrEnum.Current;
                Hsst.Hsst perAddr = new(addrEntry.Value);
                if (!perAddr.TryGet(PersistedSnapshot.SlotSubTag, out ReadOnlySpan<byte> slotData))
                    continue;

                Address addr = new(addrEntry.Key.ToArray());
                Hsst.Hsst prefixLevel = new(slotData);
                using Hsst.Hsst.Enumerator prefixEnum = prefixLevel.GetEnumerator();
                while (prefixEnum.MoveNext())
                {
                    Hsst.Hsst.KeyValueEntry prefixEntry = prefixEnum.Current;
                    byte[] prefixBytes = prefixEntry.Key.ToArray();
                    Hsst.Hsst suffixLevel = new(prefixEntry.Value);
                    using Hsst.Hsst.Enumerator suffixEnum = suffixLevel.GetEnumerator();
                    while (suffixEnum.MoveNext())
                    {
                        Hsst.Hsst.KeyValueEntry suffixEntry = suffixEnum.Current;
                        byte[] slotKey = new byte[32];
                        prefixBytes.CopyTo(slotKey.AsSpan());
                        suffixEntry.Key.CopyTo(slotKey.AsSpan(SlotPrefixLength));
                        UInt256 slot = new(slotKey, isBigEndian: true);
                        SlotValue? value = suffixEntry.Value.IsEmpty
                            ? null
                            : SlotValue.FromSpanWithoutLeadingZero(suffixEntry.Value);
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
            Hsst.Hsst outer = new(snapshotData);
            List<KeyValuePair<TreePath, TrieNode>> list = [];

            // Column 0x05: TopNodes (path length 0-5)
            if (outer.TryGet(PersistedSnapshot.StateTopNodesTag, out ReadOnlySpan<byte> topColumn))
            {
                Hsst.Hsst hsst = new(topColumn);
                using Hsst.Hsst.Enumerator e = hsst.GetEnumerator(snapshot.CreateColumnReadahead(PersistedSnapshot.StateTopNodesTag));
                while (e.MoveNext())
                {
                    Hsst.Hsst.KeyValueEntry entry = e.Current;
                    TreePath path = TreePath.DecodeWith3Byte(entry.Key);
                    TryResolveNodeRef(entry.Value, out ReadOnlySpan<byte> resolved,
                        snapshot.ReferencedSnapshotsLookup, snapshot.HasNodeRefs);
                    list.Add(new(path, new TrieNode(NodeType.Unknown, resolved.ToArray())));
                }
            }

            // Column 0x03: CompactNodes (path length 6-15)
            if (outer.TryGet(PersistedSnapshot.StateNodeTag, out ReadOnlySpan<byte> compactColumn))
            {
                Hsst.Hsst hsst = new(compactColumn);
                using Hsst.Hsst.Enumerator e = hsst.GetEnumerator(snapshot.CreateColumnReadahead(PersistedSnapshot.StateNodeTag));
                while (e.MoveNext())
                {
                    Hsst.Hsst.KeyValueEntry entry = e.Current;
                    TreePath path = DecodeCompactTreePath(entry.Key);
                    TryResolveNodeRef(entry.Value, out ReadOnlySpan<byte> resolved,
                        snapshot.ReferencedSnapshotsLookup, snapshot.HasNodeRefs);
                    list.Add(new(path, new TrieNode(NodeType.Unknown, resolved.ToArray())));
                }
            }

            // Column 0x06: Fallbacks (path length 16+)
            if (outer.TryGet(PersistedSnapshot.StateNodeFallbackTag, out ReadOnlySpan<byte> fallbackColumn))
            {
                Hsst.Hsst hsst = new(fallbackColumn);
                using Hsst.Hsst.Enumerator e = hsst.GetEnumerator(snapshot.CreateColumnReadahead(PersistedSnapshot.StateNodeFallbackTag));
                while (e.MoveNext())
                {
                    Hsst.Hsst.KeyValueEntry entry = e.Current;
                    TreePath path = new(new ValueHash256(entry.Key[..32]), entry.Key[32]);
                    TryResolveNodeRef(entry.Value, out ReadOnlySpan<byte> resolved,
                        snapshot.ReferencedSnapshotsLookup, snapshot.HasNodeRefs);
                    list.Add(new(path, new TrieNode(NodeType.Unknown, resolved.ToArray())));
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
            Hsst.Hsst outer = new(snapshotData);
            List<KeyValuePair<(Hash256AsKey, TreePath), TrieNode>> list = [];

            // Column 0x07: StorageNode (path ≤15, compact 8-byte key)
            if (outer.TryGet(PersistedSnapshot.StorageNodeTag, out ReadOnlySpan<byte> nodeColumn))
            {
                Hsst.IHsstReadahead? storageNodeReadahead = snapshot.CreateColumnReadahead(PersistedSnapshot.StorageNodeTag);
                Hsst.Hsst hashLevel = new(nodeColumn);
                using Hsst.Hsst.Enumerator hashEnum = hashLevel.GetEnumerator(storageNodeReadahead);
                while (hashEnum.MoveNext())
                {
                    Hsst.Hsst.KeyValueEntry hashEntry = hashEnum.Current;
                    Hash256 addressHash = DecodeAddressHash(hashEntry.Key);
                    Hsst.Hsst innerHsst = new(hashEntry.Value);
                    using Hsst.Hsst.Enumerator pathEnum = innerHsst.GetEnumerator(storageNodeReadahead);
                    while (pathEnum.MoveNext())
                    {
                        Hsst.Hsst.KeyValueEntry pathEntry = pathEnum.Current;
                        TreePath path = DecodeCompactTreePath(pathEntry.Key);
                        TryResolveNodeRef(pathEntry.Value, out ReadOnlySpan<byte> resolved,
                            snapshot.ReferencedSnapshotsLookup, snapshot.HasNodeRefs);
                        list.Add(new((addressHash, path), new TrieNode(NodeType.Unknown, resolved.ToArray())));
                    }
                }
            }

            // Column 0x08: StorageNodeFallback (path ≥16, 33-byte key)
            if (outer.TryGet(PersistedSnapshot.StorageNodeFallbackTag, out ReadOnlySpan<byte> fallbackColumn))
            {
                Hsst.IHsstReadahead? storageFallbackReadahead = snapshot.CreateColumnReadahead(PersistedSnapshot.StorageNodeFallbackTag);
                Hsst.Hsst hashLevel = new(fallbackColumn);
                using Hsst.Hsst.Enumerator hashEnum = hashLevel.GetEnumerator(storageFallbackReadahead);
                while (hashEnum.MoveNext())
                {
                    Hsst.Hsst.KeyValueEntry hashEntry = hashEnum.Current;
                    Hash256 addressHash = DecodeAddressHash(hashEntry.Key);
                    Hsst.Hsst innerHsst = new(hashEntry.Value);
                    using Hsst.Hsst.Enumerator pathEnum = innerHsst.GetEnumerator(storageFallbackReadahead);
                    while (pathEnum.MoveNext())
                    {
                        Hsst.Hsst.KeyValueEntry pathEntry = pathEnum.Current;
                        TreePath path = new(new ValueHash256(pathEntry.Key[..32]), pathEntry.Key[32]);
                        TryResolveNodeRef(pathEntry.Value, out ReadOnlySpan<byte> resolved,
                            snapshot.ReferencedSnapshotsLookup, snapshot.HasNodeRefs);
                        list.Add(new((addressHash, path), new TrieNode(NodeType.Unknown, resolved.ToArray())));
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
