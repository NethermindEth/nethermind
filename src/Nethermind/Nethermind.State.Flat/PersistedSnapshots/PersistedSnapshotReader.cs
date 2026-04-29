// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.State.Flat.Hsst;
using Nethermind.Trie;

namespace Nethermind.State.Flat.PersistedSnapshots;

/// <summary>
/// Static decoding/reading helpers for persisted-snapshot HSST data. All "read by key"
/// helpers consume an <see cref="IHsstByteReader{TPin}"/> and emit <see cref="Bound"/>s;
/// callers materialise spans from the reader as needed. Streaming column scans live in
/// <see cref="PersistedSnapshotScanner"/>.
/// </summary>
public static class PersistedSnapshotReader
{
    private const int TopPathThreshold = 5;
    private const int CompactPathThreshold = 15;
    private const int StorageHashPrefixLength = 20;
    private const int SlotPrefixLength = 30;

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
    /// </summary>
    internal static bool TryLoadStateNodeRlp<TReader, TPin>(scoped in TReader reader, scoped in TreePath path, out Bound bound)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        if (path.Length <= TopPathThreshold)
        {
            Span<byte> key = stackalloc byte[3];
            path.EncodeWith3Byte(key);
            return TryGetFromColumn<TReader, TPin>(in reader, PersistedSnapshot.StateTopNodesTag, key, out bound);
        }
        if (path.Length <= CompactPathThreshold)
        {
            Span<byte> key = stackalloc byte[8];
            path.EncodeWith8Byte(key);
            return TryGetFromColumn<TReader, TPin>(in reader, PersistedSnapshot.StateNodeTag, key, out bound);
        }
        Span<byte> fullKey = stackalloc byte[33];
        path.Path.Bytes.CopyTo(fullKey);
        fullKey[32] = (byte)path.Length;
        return TryGetFromColumn<TReader, TPin>(in reader, PersistedSnapshot.StateNodeFallbackTag, fullKey, out bound);
    }

    /// <summary>
    /// Look up a storage-trie node by hash + tree path. Same caller-resolves-NodeRef contract
    /// as <see cref="TryLoadStateNodeRlp"/>.
    /// </summary>
    internal static bool TryLoadStorageNodeRlp<TReader, TPin>(scoped in TReader reader, Hash256 address, in TreePath path, out Bound bound)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        if (path.Length <= CompactPathThreshold)
        {
            Span<byte> key = stackalloc byte[8];
            path.EncodeWith8Byte(key);
            return TryGetNestedValue<TReader, TPin>(in reader, PersistedSnapshot.StorageNodeTag, address.Bytes[..StorageHashPrefixLength], key, out bound);
        }
        Span<byte> fullKey = stackalloc byte[33];
        path.Path.Bytes.CopyTo(fullKey);
        fullKey[32] = (byte)path.Length;
        return TryGetNestedValue<TReader, TPin>(in reader, PersistedSnapshot.StorageNodeFallbackTag, address.Bytes[..StorageHashPrefixLength], fullKey, out bound);
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
}
