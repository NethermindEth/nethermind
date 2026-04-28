// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
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
        if (!TryGetPerAddressHsst(data, address.Bytes, out ReadOnlySpan<byte> perAddrData))
        {
            accountRlp = default;
            return false;
        }
        Hsst.Hsst perAddr = new(perAddrData);
        return perAddr.TryGet(PersistedSnapshot.AccountSubTag, out accountRlp);
    }

    internal static bool TryGetSlot(ReadOnlySpan<byte> data, Address address, in UInt256 index, [UnscopedRef] out ReadOnlySpan<byte> slotValue)
    {
        if (!TryGetPerAddressHsst(data, address.Bytes, out ReadOnlySpan<byte> perAddrData))
        {
            slotValue = default;
            return false;
        }
        Hsst.Hsst perAddr = new(perAddrData);
        if (!perAddr.TryGet(PersistedSnapshot.SlotSubTag, out ReadOnlySpan<byte> slotData))
        {
            slotValue = default;
            return false;
        }
        Span<byte> slotKey = stackalloc byte[32];
        index.ToBigEndian(slotKey);
        Hsst.Hsst prefixLevel = new(slotData);
        if (!prefixLevel.TryGet(slotKey[..SlotPrefixLength], out ReadOnlySpan<byte> suffixData))
        {
            slotValue = default;
            return false;
        }
        Hsst.Hsst suffixLevel = new(suffixData);
        return suffixLevel.TryGet(slotKey[SlotPrefixLength..], out slotValue);
    }

    internal static bool IsSelfDestructed(ReadOnlySpan<byte> data, Address address)
    {
        if (!TryGetPerAddressHsst(data, address.Bytes, out ReadOnlySpan<byte> perAddrData))
            return false;
        Hsst.Hsst perAddr = new(perAddrData);
        return perAddr.TryGet(PersistedSnapshot.SelfDestructSubTag, out _);
    }

    internal static bool? TryGetSelfDestructFlag(ReadOnlySpan<byte> data, Address address)
    {
        if (!TryGetPerAddressHsst(data, address.Bytes, out ReadOnlySpan<byte> perAddrData))
            return null;
        Hsst.Hsst perAddr = new(perAddrData);
        if (!perAddr.TryGet(PersistedSnapshot.SelfDestructSubTag, out ReadOnlySpan<byte> value))
            return null;
        return value.Length > 0 && value[0] == 0x01;
    }

    private static bool TryGetPerAddressHsst(ReadOnlySpan<byte> data, scoped ReadOnlySpan<byte> addressBytes, out ReadOnlySpan<byte> perAddrData)
    {
        Hsst.Hsst outer = new(data);
        if (!outer.TryGet(PersistedSnapshot.AccountColumnTag, out ReadOnlySpan<byte> columnData))
        {
            perAddrData = default;
            return false;
        }
        Hsst.Hsst addressLevel = new(columnData);
        return addressLevel.TryGet(addressBytes, out perAddrData);
    }

    internal static bool TryLoadStateNodeHash(ReadOnlySpan<byte> data, scoped in TreePath path, out ValueHash256 hash)
    {
        ReadOnlySpan<byte> raw;
        if (path.Length <= TopPathThreshold)
        {
            Span<byte> key = stackalloc byte[3];
            path.EncodeWith3Byte(key);
            if (!TryGetFromColumn(data, PersistedSnapshot.StateTopNodesTag, key, out raw)) { hash = default; return false; }
        }
        else if (path.Length <= CompactPathThreshold)
        {
            Span<byte> key = stackalloc byte[8];
            path.EncodeWith8Byte(key);
            if (!TryGetFromColumn(data, PersistedSnapshot.StateNodeTag, key, out raw)) { hash = default; return false; }
        }
        else
        {
            Span<byte> fullKey = stackalloc byte[33];
            path.Path.Bytes.CopyTo(fullKey);
            fullKey[32] = (byte)path.Length;
            if (!TryGetFromColumn(data, PersistedSnapshot.StateNodeFallbackTag, fullKey, out raw)) { hash = default; return false; }
        }
        hash = new ValueHash256(raw);
        return true;
    }

    internal static bool TryLoadStorageNodeHash(ReadOnlySpan<byte> data, Hash256 address, in TreePath path, out ValueHash256 hash)
    {
        ReadOnlySpan<byte> raw;
        if (path.Length <= CompactPathThreshold)
        {
            Span<byte> key = stackalloc byte[8];
            path.EncodeWith8Byte(key);
            if (!TryGetNestedValue(data, PersistedSnapshot.StorageNodeTag, address.Bytes[..StorageHashPrefixLength], key, out raw)) { hash = default; return false; }
        }
        else
        {
            Span<byte> fullKey = stackalloc byte[33];
            path.Path.Bytes.CopyTo(fullKey);
            fullKey[32] = (byte)path.Length;
            if (!TryGetNestedValue(data, PersistedSnapshot.StorageNodeFallbackTag, address.Bytes[..StorageHashPrefixLength], fullKey, out raw)) { hash = default; return false; }
        }
        hash = new ValueHash256(raw);
        return true;
    }

    internal static bool CheckForestSpilledFlag(ReadOnlySpan<byte> data)
    {
        Hsst.Hsst outer = new(data);
        if (!outer.TryGet(PersistedSnapshot.MetadataTag, out ReadOnlySpan<byte> metaColumn)) return false;
        Hsst.Hsst inner = new(metaColumn);
        return inner.TryGet("forest_spilled"u8, out _);
    }

    private static bool TryGetFromColumn(ReadOnlySpan<byte> data, scoped ReadOnlySpan<byte> tag, scoped ReadOnlySpan<byte> entityKey, scoped out ReadOnlySpan<byte> value)
    {
        Hsst.Hsst outer = new(data);
        if (!outer.TryGet(tag, out ReadOnlySpan<byte> columnData))
        {
            value = default;
            return false;
        }

        Hsst.Hsst inner = new(columnData);
        return inner.TryGet(entityKey, out value);
    }

    private static bool TryGetNestedValue(ReadOnlySpan<byte> data, scoped ReadOnlySpan<byte> tag, scoped ReadOnlySpan<byte> addressKey, scoped ReadOnlySpan<byte> entityKey, out ReadOnlySpan<byte> value)
    {
        Hsst.Hsst outer = new(data);
        if (!outer.TryGet(tag, out ReadOnlySpan<byte> columnData))
        {
            value = default;
            return false;
        }

        Hsst.Hsst addressLevel = new(columnData);
        if (!addressLevel.TryGet(addressKey, out ReadOnlySpan<byte> innerData))
        {
            value = default;
            return false;
        }

        Hsst.Hsst inner = new(innerData);
        return inner.TryGet(entityKey, out value);
    }

    private static bool TryGetDoubleNestedValue(
        ReadOnlySpan<byte> data,
        scoped ReadOnlySpan<byte> tag,
        scoped ReadOnlySpan<byte> addressKey,
        scoped ReadOnlySpan<byte> prefixKey,
        scoped ReadOnlySpan<byte> suffixKey,
        out ReadOnlySpan<byte> value)
    {
        Hsst.Hsst outer = new(data);
        if (!outer.TryGet(tag, out ReadOnlySpan<byte> columnData))
        {
            value = default;
            return false;
        }

        Hsst.Hsst addressLevel = new(columnData);
        if (!addressLevel.TryGet(addressKey, out ReadOnlySpan<byte> prefixData))
        {
            value = default;
            return false;
        }

        Hsst.Hsst prefixLevel = new(prefixData);
        if (!prefixLevel.TryGet(prefixKey, out ReadOnlySpan<byte> suffixData))
        {
            value = default;
            return false;
        }

        Hsst.Hsst suffixLevel = new(suffixData);
        return suffixLevel.TryGet(suffixKey, out value);
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

    public readonly ref struct SelfDestructEnumerable(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        public readonly SelfDestructEnumerator GetEnumerator() => new(_data);
    }

    public ref struct SelfDestructEnumerator : IDisposable
    {
        private readonly KeyValuePair<AddressAsKey, bool>[] _entries;
        private int _index;

        public SelfDestructEnumerator(ReadOnlySpan<byte> snapshotData)
        {
            _index = -1;
            Hsst.Hsst outer = new(snapshotData);
            if (!outer.TryGet(PersistedSnapshot.AccountColumnTag, out ReadOnlySpan<byte> column))
            {
                _entries = [];
                return;
            }

            List<KeyValuePair<AddressAsKey, bool>> list = [];
            Hsst.Hsst addressLevel = new(column);
            using Hsst.Hsst.Enumerator addrEnum = addressLevel.GetEnumerator();
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

    public readonly ref struct AccountEnumerable(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        public readonly AccountEnumerator GetEnumerator() => new(_data);
    }

    public ref struct AccountEnumerator : IDisposable
    {
        private readonly KeyValuePair<AddressAsKey, Account?>[] _entries;
        private int _index;

        public AccountEnumerator(ReadOnlySpan<byte> snapshotData)
        {
            _index = -1;
            Hsst.Hsst outer = new(snapshotData);
            if (!outer.TryGet(PersistedSnapshot.AccountColumnTag, out ReadOnlySpan<byte> column))
            {
                _entries = [];
                return;
            }

            List<KeyValuePair<AddressAsKey, Account?>> list = [];
            Hsst.Hsst addressLevel = new(column);
            using Hsst.Hsst.Enumerator addrEnum = addressLevel.GetEnumerator();
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

    public readonly ref struct StorageEnumerable(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        public readonly StorageEnumerator GetEnumerator() => new(_data);
    }

    public ref struct StorageEnumerator : IDisposable
    {
        private readonly KeyValuePair<(AddressAsKey, UInt256), SlotValue?>[] _entries;
        private int _index;

        public StorageEnumerator(ReadOnlySpan<byte> snapshotData)
        {
            _index = -1;
            Hsst.Hsst outer = new(snapshotData);
            if (!outer.TryGet(PersistedSnapshot.AccountColumnTag, out ReadOnlySpan<byte> column))
            {
                _entries = [];
                return;
            }

            List<KeyValuePair<(AddressAsKey, UInt256), SlotValue?>> list = [];
            Hsst.Hsst addressLevel = new(column);
            using Hsst.Hsst.Enumerator addrEnum = addressLevel.GetEnumerator();
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
        private readonly KeyValuePair<TreePath, ValueHash256>[] _entries;
        private int _index;

        public StateNodeEnumerator(PersistedSnapshot snapshot)
        {
            _index = -1;
            ReadOnlySpan<byte> snapshotData = snapshot.GetSpan();
            Hsst.Hsst outer = new(snapshotData);
            List<KeyValuePair<TreePath, ValueHash256>> list = [];

            // Column 0x05: TopNodes (path length 0-5)
            if (outer.TryGet(PersistedSnapshot.StateTopNodesTag, out ReadOnlySpan<byte> topColumn))
            {
                Hsst.Hsst hsst = new(topColumn);
                using Hsst.Hsst.Enumerator e = hsst.GetEnumerator();
                while (e.MoveNext())
                {
                    Hsst.Hsst.KeyValueEntry entry = e.Current;
                    TreePath path = TreePath.DecodeWith3Byte(entry.Key);
                    list.Add(new(path, new ValueHash256(entry.Value)));
                }
            }

            // Column 0x03: CompactNodes (path length 6-15)
            if (outer.TryGet(PersistedSnapshot.StateNodeTag, out ReadOnlySpan<byte> compactColumn))
            {
                Hsst.Hsst hsst = new(compactColumn);
                using Hsst.Hsst.Enumerator e = hsst.GetEnumerator();
                while (e.MoveNext())
                {
                    Hsst.Hsst.KeyValueEntry entry = e.Current;
                    TreePath path = DecodeCompactTreePath(entry.Key);
                    list.Add(new(path, new ValueHash256(entry.Value)));
                }
            }

            // Column 0x06: Fallbacks (path length 16+)
            if (outer.TryGet(PersistedSnapshot.StateNodeFallbackTag, out ReadOnlySpan<byte> fallbackColumn))
            {
                Hsst.Hsst hsst = new(fallbackColumn);
                using Hsst.Hsst.Enumerator e = hsst.GetEnumerator();
                while (e.MoveNext())
                {
                    Hsst.Hsst.KeyValueEntry entry = e.Current;
                    TreePath path = new(new ValueHash256(entry.Key[..32]), entry.Key[32]);
                    list.Add(new(path, new ValueHash256(entry.Value)));
                }
            }

            _entries = [.. list];
        }

        public bool MoveNext() => ++_index < _entries.Length;
        public readonly KeyValuePair<TreePath, ValueHash256> Current => _entries[_index];
        public readonly void Dispose() { }
    }

    public readonly struct StorageNodeEnumerable(PersistedSnapshot snapshot)
    {
        private readonly PersistedSnapshot _snapshot = snapshot;
        public StorageNodeEnumerator GetEnumerator() => new(_snapshot);
    }

    public ref struct StorageNodeEnumerator : IDisposable
    {
        private readonly KeyValuePair<(Hash256AsKey, TreePath), ValueHash256>[] _entries;
        private int _index;

        public StorageNodeEnumerator(PersistedSnapshot snapshot)
        {
            _index = -1;
            ReadOnlySpan<byte> snapshotData = snapshot.GetSpan();
            Hsst.Hsst outer = new(snapshotData);
            List<KeyValuePair<(Hash256AsKey, TreePath), ValueHash256>> list = [];

            // Column 0x07: StorageNode (path ≤15, compact 8-byte key)
            if (outer.TryGet(PersistedSnapshot.StorageNodeTag, out ReadOnlySpan<byte> nodeColumn))
            {
                Hsst.Hsst hashLevel = new(nodeColumn);
                using Hsst.Hsst.Enumerator hashEnum = hashLevel.GetEnumerator();
                while (hashEnum.MoveNext())
                {
                    Hsst.Hsst.KeyValueEntry hashEntry = hashEnum.Current;
                    Hash256 addressHash = DecodeAddressHash(hashEntry.Key);
                    Hsst.Hsst innerHsst = new(hashEntry.Value);
                    using Hsst.Hsst.Enumerator pathEnum = innerHsst.GetEnumerator();
                    while (pathEnum.MoveNext())
                    {
                        Hsst.Hsst.KeyValueEntry pathEntry = pathEnum.Current;
                        TreePath path = DecodeCompactTreePath(pathEntry.Key);
                        list.Add(new((addressHash, path), new ValueHash256(pathEntry.Value)));
                    }
                }
            }

            // Column 0x08: StorageNodeFallback (path ≥16, 33-byte key)
            if (outer.TryGet(PersistedSnapshot.StorageNodeFallbackTag, out ReadOnlySpan<byte> fallbackColumn))
            {
                Hsst.Hsst hashLevel = new(fallbackColumn);
                using Hsst.Hsst.Enumerator hashEnum = hashLevel.GetEnumerator();
                while (hashEnum.MoveNext())
                {
                    Hsst.Hsst.KeyValueEntry hashEntry = hashEnum.Current;
                    Hash256 addressHash = DecodeAddressHash(hashEntry.Key);
                    Hsst.Hsst innerHsst = new(hashEntry.Value);
                    using Hsst.Hsst.Enumerator pathEnum = innerHsst.GetEnumerator();
                    while (pathEnum.MoveNext())
                    {
                        Hsst.Hsst.KeyValueEntry pathEntry = pathEnum.Current;
                        TreePath path = new(new ValueHash256(pathEntry.Key[..32]), pathEntry.Key[32]);
                        list.Add(new((addressHash, path), new ValueHash256(pathEntry.Value)));
                    }
                }
            }

            _entries = [.. list];
        }

        public bool MoveNext() => ++_index < _entries.Length;
        public readonly KeyValuePair<(Hash256AsKey, TreePath), ValueHash256> Current => _entries[_index];
        public readonly void Dispose() { }
    }
}
