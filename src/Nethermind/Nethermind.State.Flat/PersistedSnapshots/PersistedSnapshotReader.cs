// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
/// Static decoding/reading helpers and enumerators for persisted-snapshot HSST data.
/// All "read by key" helpers consume an <see cref="IHsstByteReader{TPin}"/> and emit
/// <see cref="Bound"/>s; callers materialise spans from the reader as needed.
/// </summary>
public static class PersistedSnapshotReader
{
    private const int TopPathThreshold = 5;
    private const int CompactPathThreshold = 15;
    private const int StorageHashPrefixLength = 20;
    private const int SlotPrefixLength = 30;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<byte> SliceFromBound(ReadOnlySpan<byte> data, Bound b) =>
        data.Slice((int)b.Offset, b.Length);

    internal static bool TryGetAccount<TReader, TPin>(scoped in TReader reader, Address address, out Bound accountBound)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        using HsstReader<TReader, TPin> r = new(in reader);
        if (!r.TrySeek(PersistedSnapshot.AccountColumnTag, out _) ||
            !r.TrySeek(address.Bytes, out _) ||
            !r.TrySeek(PersistedSnapshot.AccountSubTag, out _))
        {
            accountBound = default;
            return false;
        }
        accountBound = r.GetBound();
        return true;
    }

    internal static bool TryGetSlot<TReader, TPin>(scoped in TReader reader, Address address, in UInt256 index, out Bound slotBound)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        using HsstReader<TReader, TPin> r = new(in reader);
        Span<byte> slotKey = stackalloc byte[32];
        index.ToBigEndian(slotKey);
        if (!r.TrySeek(PersistedSnapshot.AccountColumnTag, out _) ||
            !r.TrySeek(address.Bytes, out _) ||
            !r.TrySeek(PersistedSnapshot.SlotSubTag, out _) ||
            !r.TrySeek(slotKey[..SlotPrefixLength], out _) ||
            !r.TrySeek(slotKey[SlotPrefixLength..], out _))
        {
            slotBound = default;
            return false;
        }
        slotBound = r.GetBound();
        return true;
    }

    internal static bool IsSelfDestructed<TReader, TPin>(scoped in TReader reader, Address address)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        using HsstReader<TReader, TPin> r = new(in reader);
        return r.TrySeek(PersistedSnapshot.AccountColumnTag, out _)
            && r.TrySeek(address.Bytes, out _)
            && r.TrySeek(PersistedSnapshot.SelfDestructSubTag, out _);
    }

    internal static bool? TryGetSelfDestructFlag<TReader, TPin>(scoped in TReader reader, Address address)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        using HsstReader<TReader, TPin> r = new(in reader);
        if (!r.TrySeek(PersistedSnapshot.AccountColumnTag, out _) ||
            !r.TrySeek(address.Bytes, out _) ||
            !r.TrySeek(PersistedSnapshot.SelfDestructSubTag, out _))
            return null;
        Bound b = r.GetBound();
        if (b.Length == 0) return false;
        Span<byte> oneByte = stackalloc byte[1];
        if (!reader.TryRead(b.Offset, oneByte)) return false;
        return oneByte[0] == 0x01;
    }

    /// <summary>
    /// Look up a state-trie node by tree path. Returns the local value <see cref="Bound"/>
    /// — caller (<see cref="PersistedSnapshot"/>) checks <c>HasNodeRefs</c>, decodes the
    /// NodeRef when present, and does the cross-snapshot dereference.
    ///
    /// Span-based at the public layer because C#'s ref-safety analysis on generic
    /// <c>allows ref struct</c> readers loses the "out Bound is value-type" property when
    /// the caller's <c>out ReadOnlySpan&lt;byte&gt;</c> needs to escape across a loop;
    /// internally we still use the reader-shaped helpers.
    /// </summary>
    internal static bool TryLoadStateNodeRlp(ReadOnlySpan<byte> data, scoped in TreePath path, out Bound bound)
    {
        SpanByteReader reader = new(data);
        if (path.Length <= TopPathThreshold)
        {
            Span<byte> key = stackalloc byte[3];
            path.EncodeWith3Byte(key);
            return TryGetFromColumn<SpanByteReader, NoOpPin>(in reader, PersistedSnapshot.StateTopNodesTag, key, out bound);
        }
        if (path.Length <= CompactPathThreshold)
        {
            Span<byte> key = stackalloc byte[8];
            path.EncodeWith8Byte(key);
            return TryGetFromColumn<SpanByteReader, NoOpPin>(in reader, PersistedSnapshot.StateNodeTag, key, out bound);
        }
        Span<byte> fullKey = stackalloc byte[33];
        path.Path.Bytes.CopyTo(fullKey);
        fullKey[32] = (byte)path.Length;
        return TryGetFromColumn<SpanByteReader, NoOpPin>(in reader, PersistedSnapshot.StateNodeFallbackTag, fullKey, out bound);
    }

    /// <summary>
    /// Look up a storage-trie node by hash + tree path. Same caller-resolves-NodeRef contract
    /// and same span-input rationale as <see cref="TryLoadStateNodeRlp"/>.
    /// </summary>
    internal static bool TryLoadStorageNodeRlp(ReadOnlySpan<byte> data, Hash256 address, in TreePath path, out Bound bound)
    {
        SpanByteReader reader = new(data);
        if (path.Length <= CompactPathThreshold)
        {
            Span<byte> key = stackalloc byte[8];
            path.EncodeWith8Byte(key);
            return TryGetNestedValue<SpanByteReader, NoOpPin>(in reader, PersistedSnapshot.StorageNodeTag, address.Bytes[..StorageHashPrefixLength], key, out bound);
        }
        Span<byte> fullKey = stackalloc byte[33];
        path.Path.Bytes.CopyTo(fullKey);
        fullKey[32] = (byte)path.Length;
        return TryGetNestedValue<SpanByteReader, NoOpPin>(in reader, PersistedSnapshot.StorageNodeFallbackTag, address.Bytes[..StorageHashPrefixLength], fullKey, out bound);
    }

    internal static bool CheckHasNodeRefsFlag<TReader, TPin>(scoped in TReader reader)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        using HsstReader<TReader, TPin> r = new(in reader);
        return r.TrySeek(PersistedSnapshot.MetadataTag, out _)
            && r.TrySeek("noderefs"u8, out _);
    }

    internal static int[]? ReadRefIdsFromMetadata<TReader, TPin>(scoped in TReader reader)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        using HsstReader<TReader, TPin> r = new(in reader);
        if (!r.TrySeek(PersistedSnapshot.MetadataTag, out _) ||
            !r.TrySeek("ref_ids"u8, out _))
            return null;
        Bound b = r.GetBound();
        if (b.Length == 0 || b.Length % 4 != 0) return null;
        int count = b.Length / 4;
        Span<byte> buf = stackalloc byte[256];
        if (b.Length > buf.Length)
            buf = new byte[b.Length];
        if (!reader.TryRead(b.Offset, buf[..b.Length])) return null;
        int[] ids = new int[count];
        for (int i = 0; i < count; i++)
            ids[i] = BitConverter.ToInt32(buf.Slice(i * 4, 4));
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

    private static bool TryGetFromColumn<TReader, TPin>(in TReader reader, scoped ReadOnlySpan<byte> tag, scoped ReadOnlySpan<byte> entityKey, out Bound bound)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        using HsstReader<TReader, TPin> r = new(in reader);
        if (!r.TrySeek(tag, out _) || !r.TrySeek(entityKey, out _))
        {
            bound = default;
            return false;
        }
        bound = r.GetBound();
        return true;
    }

    private static bool TryGetNestedValue<TReader, TPin>(in TReader reader, scoped ReadOnlySpan<byte> tag, scoped ReadOnlySpan<byte> addressKey, scoped ReadOnlySpan<byte> entityKey, out Bound bound)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        using HsstReader<TReader, TPin> r = new(in reader);
        if (!r.TrySeek(tag, out _) || !r.TrySeek(addressKey, out _) || !r.TrySeek(entityKey, out _))
        {
            bound = default;
            return false;
        }
        bound = r.GetBound();
        return true;
    }

    private static bool TryGetDoubleNestedValue<TReader, TPin>(
        scoped in TReader reader,
        scoped ReadOnlySpan<byte> tag,
        scoped ReadOnlySpan<byte> addressKey,
        scoped ReadOnlySpan<byte> prefixKey,
        scoped ReadOnlySpan<byte> suffixKey,
        out Bound bound)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        using HsstReader<TReader, TPin> r = new(in reader);
        if (!r.TrySeek(tag, out _) ||
            !r.TrySeek(addressKey, out _) ||
            !r.TrySeek(prefixKey, out _) ||
            !r.TrySeek(suffixKey, out _))
        {
            bound = default;
            return false;
        }
        bound = r.GetBound();
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
            SpanByteReader reader = snapshot.CreateReader();
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
            SpanByteReader reader = snapshot.CreateReader();
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
            SpanByteReader reader = snapshot.CreateReader();
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
            SpanByteReader reader = snapshot.CreateReader();
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
                        ReadOnlySpan<byte> resolved = snapshot.ResolveValueAt(entry.ValueBound);
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
                        ReadOnlySpan<byte> resolved = snapshot.ResolveValueAt(entry.ValueBound);
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
                        ReadOnlySpan<byte> resolved = snapshot.ResolveValueAt(entry.ValueBound);
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
            SpanByteReader reader = snapshot.CreateReader();
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
                            ReadOnlySpan<byte> resolved = snapshot.ResolveValueAt(pathEntry.ValueBound);
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
                            ReadOnlySpan<byte> resolved = snapshot.ResolveValueAt(pathEntry.ValueBound);
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
