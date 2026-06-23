// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.PersistedSnapshots;

/// <summary>
/// Shared on-disk vocabulary for the persisted snapshot's single-level sorted table: value-marker
/// bytes, metadata key names, and layout-width constants. The verbose key encoding (column /
/// subcolumn tags stored as <c>255 − tag</c>) lives in <see cref="PersistedSnapshotKey"/>; this type
/// holds only the format constants that producers (<see cref="PersistedSnapshotBuilder"/>,
/// <see cref="PersistedSnapshotMerger"/>) and consumers (<see cref="PersistedSnapshot"/>,
/// <see cref="PersistedSnapshotReader"/>, <see cref="PersistedSnapshotScanner"/>) must agree on.
/// </summary>
internal static class PersistedSnapshotTags
{
    // Per-addressHash column outer key width — first 20 bytes of Keccak(address).
    internal const int AddressHashPrefixLength = 20;

    // Value markers. Self-destruct: [0x00] destructed, [0x01] newly created (absent = key not
    // present). Account: [0x00] explicitly deleted, otherwise slim account RLP (first byte 0xc0+,
    // so the deleted marker is unambiguous against any RLP).
    internal static readonly byte[] SelfDestructDestructedMarker = [0x00];
    internal static readonly byte[] SelfDestructNewMarker = [0x01];
    internal static readonly byte[] AccountDeletedMarker = [0x00];
    internal const byte SelfDestructDestructedMarkerByte = 0x00;
    internal const byte AccountDeletedMarkerByte = 0x00;

    // Metadata key names. NUL-padded to a fixed 10 bytes (the longest original key, "from_block");
    // padding preserves sort order because no original key is a prefix of another.
    internal const int MetadataKeyLength = 10;
    // Base snapshots only: the contiguous trie-RLP run in the single blob arena they wrote into,
    // serialized as a BlobRange; absent on compacted / CompactSized snapshots (BlobRange.None).
    internal static readonly byte[] MetadataBlobRangeKey = "blob_range"u8.ToArray();
    internal static readonly byte[] MetadataFromBlockKey = "from_block"u8.ToArray();
    internal static readonly byte[] MetadataFromHashKey = "from_hash\0"u8.ToArray();
    internal static readonly byte[] MetadataNodeRefsKey = "noderefs\0\0"u8.ToArray();
    internal static readonly byte[] MetadataToBlockKey = "to_block\0\0"u8.ToArray();
    internal static readonly byte[] MetadataToHashKey = "to_hash\0\0\0"u8.ToArray();
    internal static readonly byte[] MetadataVersionKey = "version\0\0\0"u8.ToArray();

    // Referenced blob-arena ids are stored as one record per id (key = ref-id column + id; see
    // PersistedSnapshotKey.WriteRefIdKey) rather than a single list value, so they merge/dedup
    // through the normal N-way merge and iterate like any other records. This is the per-id value.
    internal static readonly byte[] RefIdValue = [0x01];

    // On-disk format version, written as the value of MetadataVersionKey by the builder and copied
    // through by the merger. Bump when the on-disk layout changes.
    // v5: single-level sorted table (replaces the columnar HSST format).
    internal static readonly byte[] MetadataFormatVersion = [0x05];

    // Largest RLP encoding of a slot value: a 32-byte string is a 1-byte prefix (0xa0) plus 32
    // bytes. Mirrors BaseFlatPersistence.RlpSlotValueBufferSize.
    internal const int RlpSlotValueBufferSize = SlotValue.ByteCount + 1;

    // Presence marker for MetadataNodeRefsKey. The key itself is the signal; the value just
    // satisfies the non-empty-value requirement.
    internal static readonly byte[] MetadataNodeRefsPresentMarker = [0x01];
}
