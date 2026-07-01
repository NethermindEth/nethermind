// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie;

namespace Nethermind.State.Flat.PersistedSnapshots;

/// <summary>
/// Materializes the fully-verbose, single-level sorted-table keys for a persisted snapshot and
/// classifies them on read. The on-disk table is a plain ascending byte-sorted map (see
/// <see cref="Sorted.SortedTable"/>); to reproduce the reverse-tag emission order that the columnar
/// builder/compacter use (outer columns and per-entity sub-tags descend, entity bytes ascend), the
/// column and subcolumn tag bytes are stored as <c>255 − tag</c>. Everything else is natural.
/// </summary>
/// <remarks>
/// Key shapes (tag bytes shown as their stored <c>255 − tag</c> value):
/// <code>
///   Storage node : FA + addrHash(20) + {FF top | FE compact | FD fallback} + path
///   State node   : {FD top | FC compact | FB fallback} + path
///   Self-destruct: FE + addr(20) + FD
///   Slot         : FE + addr(20) + FE + slot(32 BE)
///   Account      : FE + addr(20) + FF
///   Metadata     : FF + name(10, NUL-padded)
/// </code>
/// Ascending byte order over these is exactly the columnar leaf-emission order.
/// </remarks>
internal static class PersistedSnapshotKey
{
    // Referenced blob-arena ids: one record per id, keyed by this column (0x00) + the id. 0x00 is
    // below every real column (0xFA..0xFF), so ref-id records sort first and iterate cheaply from
    // the table start; the value is a presence marker (PersistedSnapshotTags.RefIdValue).
    internal const byte RefIdColumn = 0x00;
    internal const int RefIdKeyLength = 1 + sizeof(ushort);

    // Column tag bytes = 255 - PersistedSnapshotTags column tag.
    internal const byte MetadataColumn = 0xFF;       // 255 - 0x00
    internal const byte AccountColumn = 0xFE;        // 255 - 0x01 (per-address: account/SD/slots)
    internal const byte StateTopColumn = 0xFD;       // 255 - 0x02
    internal const byte StateCompactColumn = 0xFC;   // 255 - 0x03
    internal const byte StateFallbackColumn = 0xFB;  // 255 - 0x04
    internal const byte StorageColumn = 0xFA;        // 255 - 0x05

    // Per-address subcolumn bytes = 255 - per-address sub-tag. Self-destruct sorts before slots so the
    // merge resolves an address's truncation barrier before the slots it filters, and can stream them.
    internal const byte AccountSub = 0xFF;           // 255 - 0x00
    internal const byte SelfDestructSub = 0xFD;      // 255 - 0x02
    internal const byte SlotSub = 0xFE;              // 255 - 0x01

    // Storage-trie subcolumn bytes = 255 - storage sub-tag.
    internal const byte StorageTopSub = 0xFF;        // 255 - 0x00
    internal const byte StorageCompactSub = 0xFE;    // 255 - 0x01
    internal const byte StorageFallbackSub = 0xFD;   // 255 - 0x02

    private const int TopPathThreshold = 7;
    private const int CompactPathThreshold = 15;

    internal const int AddressKeyLength = Address.Size;                 // 20
    internal const int AddressHashPrefixLength = PersistedSnapshotTags.AddressHashPrefixLength; // 20
    internal const int SlotLength = 32;

    /// <summary>Largest materialized key: storage fallback = 1 + 20 + 1 + 33.</summary>
    internal const int MaxKeyLength = 1 + AddressHashPrefixLength + 1 + 33;

    internal static int WriteMetadataKey(Span<byte> dst, scoped ReadOnlySpan<byte> name)
    {
        dst[0] = MetadataColumn;
        name.CopyTo(dst[1..]);
        return 1 + name.Length;
    }

    /// <summary>Materialize a referenced blob-arena id record key: <see cref="RefIdColumn"/> + the
    /// id (big-endian, so ids sort numerically).</summary>
    internal static int WriteRefIdKey(Span<byte> dst, ushort blobArenaId)
    {
        dst[0] = RefIdColumn;
        BinaryPrimitives.WriteUInt16BigEndian(dst[1..], blobArenaId);
        return RefIdKeyLength;
    }

    internal static ushort ReadRefId(scoped ReadOnlySpan<byte> key) => BinaryPrimitives.ReadUInt16BigEndian(key[1..]);

    internal static int WriteAccountKey(Span<byte> dst, scoped ReadOnlySpan<byte> address)
    {
        dst[0] = AccountColumn;
        address.CopyTo(dst[1..]);
        dst[1 + AddressKeyLength] = AccountSub;
        return 2 + AddressKeyLength;
    }

    internal static int WriteSelfDestructKey(Span<byte> dst, scoped ReadOnlySpan<byte> address)
    {
        dst[0] = AccountColumn;
        address.CopyTo(dst[1..]);
        dst[1 + AddressKeyLength] = SelfDestructSub;
        return 2 + AddressKeyLength;
    }

    internal static int WriteSlotKey(Span<byte> dst, scoped ReadOnlySpan<byte> address, scoped ReadOnlySpan<byte> slot32)
    {
        dst[0] = AccountColumn;
        address.CopyTo(dst[1..]);
        dst[1 + AddressKeyLength] = SlotSub;
        slot32.CopyTo(dst[(2 + AddressKeyLength)..]);
        return 2 + AddressKeyLength + SlotLength;
    }

    internal static int WriteStateNodeKey(Span<byte> dst, scoped in TreePath path)
    {
        if (path.Length <= TopPathThreshold)
        {
            dst[0] = StateTopColumn;
            path.EncodeWith4Byte(dst.Slice(1, 4));
            return 5;
        }
        if (path.Length <= CompactPathThreshold)
        {
            dst[0] = StateCompactColumn;
            path.EncodeWith8Byte(dst.Slice(1, 8));
            return 9;
        }
        dst[0] = StateFallbackColumn;
        path.Path.Bytes.CopyTo(dst[1..]);
        dst[33] = (byte)path.Length;
        return 34;
    }

    internal static int WriteStorageNodeKey(Span<byte> dst, scoped ReadOnlySpan<byte> addressHash, scoped in TreePath path)
    {
        dst[0] = StorageColumn;
        addressHash[..AddressHashPrefixLength].CopyTo(dst[1..]);
        int pathStart = 2 + AddressHashPrefixLength;
        if (path.Length <= TopPathThreshold)
        {
            dst[1 + AddressHashPrefixLength] = StorageTopSub;
            path.EncodeWith4Byte(dst.Slice(pathStart, 4));
            return pathStart + 4;
        }
        if (path.Length <= CompactPathThreshold)
        {
            dst[1 + AddressHashPrefixLength] = StorageCompactSub;
            path.EncodeWith8Byte(dst.Slice(pathStart, 8));
            return pathStart + 8;
        }
        dst[1 + AddressHashPrefixLength] = StorageFallbackSub;
        path.Path.Bytes.CopyTo(dst[pathStart..]);
        dst[pathStart + 32] = (byte)path.Length;
        return pathStart + 33;
    }

    // ---- read-side classification helpers (operate on a materialized key span) ----

    internal static ReadOnlySpan<byte> PerAddressAddress(ReadOnlySpan<byte> key) =>
        key.Slice(1, AddressKeyLength);

    internal static byte PerAddressSubColumn(scoped ReadOnlySpan<byte> key) => key[1 + AddressKeyLength];

    internal static ReadOnlySpan<byte> SlotKeyBytes(ReadOnlySpan<byte> key) =>
        key.Slice(2 + AddressKeyLength, SlotLength);

    internal static ReadOnlySpan<byte> StorageAddressHash(ReadOnlySpan<byte> key) =>
        key.Slice(1, AddressHashPrefixLength);

    internal static byte StorageSubColumn(scoped ReadOnlySpan<byte> key) => key[1 + AddressHashPrefixLength];

    internal static ReadOnlySpan<byte> StoragePathBytes(ReadOnlySpan<byte> key) =>
        key[(2 + AddressHashPrefixLength)..];

    internal static ReadOnlySpan<byte> StatePathBytes(ReadOnlySpan<byte> key) => key[1..];

    /// <summary>Decode a state/storage path key, given its column or subcolumn-derived stage
    /// (0 = top/4-byte, 1 = compact/8-byte, else fallback/33-byte).</summary>
    internal static TreePath DecodePath(scoped ReadOnlySpan<byte> encoded, int stage) => stage switch
    {
        0 => TreePath.DecodeWith4Byte(encoded),
        1 => TreePath.DecodeWith8Byte(encoded),
        _ => new TreePath(new ValueHash256(encoded[..32]), encoded[32]),
    };
}
