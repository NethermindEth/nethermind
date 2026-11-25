// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Eez.Posting;

/// <summary>
/// Where a postBatch bundle must land. A slot's batch is pinned to the L1 block after the slot's anchor, at the Sync
/// block's timestamp, since the Sync block carries that timestamp; a catch-up batch settles past blocks and takes the
/// next L1 block that includes it.
/// </summary>
public readonly record struct BundleTarget(ulong Block, ulong Timestamp)
{
    public static BundleTarget NextBlock => default;

    public bool IsPinned => Timestamp != 0;
}
