// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.PersistedSnapshots;

/// <summary>
/// On-disk vocabulary for the columnar persisted-snapshot HSST: outer column tags, per-address
/// sub-tags, value-marker bytes, metadata keys, and layout-width constants. All producers
/// (<see cref="PersistedSnapshotBuilder"/>, <see cref="PersistedSnapshotMerger"/>) and all
/// consumers (<see cref="PersistedSnapshot"/>, <see cref="PersistedSnapshotReader"/>,
/// <see cref="PersistedSnapshotScanner"/>) share these definitions so the encoding cannot drift
/// between write and read sides.
/// </summary>
/// <remarks>
/// Columnar layout — the outer HSST has 5 column entries, each containing an inner HSST.
/// Inner HSST keys are the entity keys without the tag prefix:
///   Column 0x00: Metadata — String key → version, block range, ref_ids list, state root values
///   Column 0x01: AddressHash (20 bytes, = Keccak(address)[..20]) → per-address HSST {
///       0x01 (AddressSubTag):         raw 20-byte Address bytes — preimage of the outer addressHash
///       0x02 (AccountSubTag):         raw account slim RLP bytes (empty = deleted account)
///       0x03 (SelfDestructSubTag):    raw SD flag bytes (empty = destructed, 0x01 = new account)
///       0x04 (SlotSubTag):            nested HSST (SlotPrefix(30) → nested HSST(SlotSuffix(2) → SlotValue))
///       0x05 (StorageFallbackSubTag): nested HSST (TreePath.Path (33 bytes) → NodeRef, path length 16+)
///       0x06 (StorageCompactSubTag):  nested HSST (TreePath (8 bytes compact) → NodeRef, path length 8-15)
///       0x07 (StorageTopSubTag):      nested HSST (TreePath (3 bytes) → NodeRef, path length 0-5)
///   }
///   Sub-tag values are arranged so the small, hot metadata (Address/Account/SelfDestruct)
///   gets the lowest byte values. The per-address inner HSST is built as a dense-byte-index
///   whose value blobs are streamed high-tag → low-tag (descending) so the storage-trie
///   blobs land at the front of the data section and the hot metadata blobs land adjacent
///   to the trailing Ends[] table, sharing OS pages with the lookup-time read.
///   Column 0x03: TreePath (8 bytes compact) → NodeRef (path length 6-15)
///   Column 0x05: TreePath (3 bytes) → NodeRef (path length 0-5)
///   Column 0x06: TreePath.Path (32 bytes) + PathLength (1 byte) → NodeRef (path length 16+)
/// </remarks>
internal static class PersistedSnapshotTags
{
    // Tag prefixes for outer HSST columns.
    internal static readonly byte[] MetadataTag = [0x00];
    internal static readonly byte[] AccountColumnTag = [0x01];
    internal static readonly byte[] StateNodeTag = [0x03];
    internal static readonly byte[] StateTopNodesTag = [0x05];
    internal static readonly byte[] StateNodeFallbackTag = [0x06];

    // Per-address column 0x01 outer key width — first 20 bytes of Keccak(address).
    internal const int AddressHashPrefixLength = 20;

    // Sub-tags within per-address HSST (column 0x01). The per-address HSST is built as a
    // dense-byte-index whose writer streams entries in strictly descending tag order, so the
    // value blobs for the hot small metadata (low tag values) end up adjacent to the trailing
    // Ends[] table — see the class-level remarks for the layout rationale.
    internal static readonly byte[] AddressSubTag = [0x01];
    internal static readonly byte[] AccountSubTag = [0x02];
    internal static readonly byte[] SelfDestructSubTag = [0x03];
    internal static readonly byte[] SlotSubTag = [0x04];
    internal static readonly byte[] StorageFallbackSubTag = [0x05];
    internal static readonly byte[] StorageCompactSubTag = [0x06];
    internal static readonly byte[] StorageTopSubTag = [0x07];

    // Single-byte companions of the sub-tag arrays above, consumed by the fast-path
    // <see cref="HsstDenseByteIndexReader.TryResolveSingleTag{TReader, TPin}"/> resolver which
    // takes the tag as a <see cref="byte"/> rather than a one-element <see cref="ReadOnlySpan{T}"/>.
    internal const byte AccountSubTagByte = 0x02;
    internal const byte SelfDestructSubTagByte = 0x03;
    internal const byte SlotSubTagByte = 0x04;
    internal const byte StorageFallbackSubTagByte = 0x05;
    internal const byte StorageCompactSubTagByte = 0x06;
    internal const byte StorageTopSubTagByte = 0x07;

    // Per-address (column 0x01) DenseByteIndex stride: max sub-tag (0x07) + 1 = 8.
    // TryResolveAll fills slots 0..7 in one pass; slot 0 is never populated and comes
    // back as a length-0 absence.
    internal const int PerAddrSubTagCount = 8;

    // Sub-tag value markers within column 0x01. Encoding for SelfDestructSubTag (0x03):
    //   absent (length 0) — no SD record in this snapshot
    //   [0x00]            — account destructed in this snapshot
    //   [0x01]            — account newly created in this snapshot
    // Encoding for AccountSubTag (0x02):
    //   absent (length 0) — no account record in this snapshot
    //   [0x00]            — account explicitly deleted in this snapshot
    //   <RLP bytes>       — present (slim account RLP; first byte is a list header 0xc0+
    //                       so the deleted-marker 0x00 is unambiguous against any RLP).
    internal static readonly byte[] SelfDestructDestructedMarker = [0x00];
    internal static readonly byte[] SelfDestructNewMarker = [0x01];
    internal static readonly byte[] AccountDeletedMarker = [0x00];
    internal const byte SelfDestructDestructedMarkerByte = 0x00;
    internal const byte SelfDestructNewMarkerByte = 0x01;
    internal const byte AccountDeletedMarkerByte = 0x00;

    // Metadata column keys. The HSST builder requires uniform key length per HSST,
    // so the original ASCII keys are NUL-padded to a fixed 10 bytes (the longest
    // original key, "from_block"). NUL-padding preserves the original sort order
    // because no original key is a prefix of any other.
    internal const int MetadataKeyLength = 10;
    internal static readonly byte[] MetadataFromBlockKey = "from_block"u8.ToArray();
    internal static readonly byte[] MetadataFromHashKey = "from_hash\0"u8.ToArray();
    internal static readonly byte[] MetadataNodeRefsKey = "noderefs\0\0"u8.ToArray();
    internal static readonly byte[] MetadataRefIdsKey = "ref_ids\0\0\0"u8.ToArray();
    internal static readonly byte[] MetadataToBlockKey = "to_block\0\0"u8.ToArray();
    internal static readonly byte[] MetadataToHashKey = "to_hash\0\0\0"u8.ToArray();
    internal static readonly byte[] MetadataVersionKey = "version\0\0\0"u8.ToArray();

    // On-disk format version, written as the value of MetadataVersionKey by the builder
    // and copied through by the merger. Bump when the columnar layout changes.
    internal static readonly byte[] MetadataFormatVersion = [0x01];

    // Presence marker for MetadataNodeRefsKey. The key itself is the signal; the value
    // just satisfies the HSST builder's non-empty-value requirement.
    internal static readonly byte[] MetadataNodeRefsPresentMarker = [0x01];
}
