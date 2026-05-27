// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.StateDiffsWriter.Storage;

/// <summary>
/// Column families for the <c>BlockDiffs</c> RocksDB database.
/// <list type="bullet">
///   <item>
///     <description>
///       <see cref="Default"/>: per-block records keyed by big-endian 8-byte block
///       number, value = RLP-encoded <see cref="Data.BlockDiffRecord"/>.
///       Pruned by <see cref="Service.DiffsPruner"/> to bound disk usage.
///     </description>
///   </item>
///   <item>
///     <description>
///       <see cref="SlotCounts"/>: per-account running storage-slot count keyed
///       by 32-byte <c>ValueHash256</c> address-hash, value = 8-byte big-endian
///       uint64. Carried alongside the per-block records because the v19 wire
///       format requires every <see cref="Data.SlotCountChange"/> entry to ship
///       both the pre- and post-block totals (the sidecar must not maintain a
///       second running count). This is the minimal residual state on the NM
///       side: O(contracts) entries, single-key writes per changed contract,
///       never pruned.
///     </description>
///   </item>
/// </list>
/// </summary>
public enum BlockDiffsColumns
{
    Default,
    SlotCounts,
}
