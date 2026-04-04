// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Network.P2P.Subprotocols.Snap.Messages;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Snap;

namespace Nethermind.Network.P2P.Subprotocols.Snap;

public static class SnapMessageLimits
{
    public const int MaxRequestHashes = 4_096;
    public const int MaxRequestAccounts = 4_096;
    public const int MaxRequestPathGroups = 4_096;
    public const int MaxRequestPathsPerGroup = 1_024;
    public const int MaxResponseAccounts = 131_072;
    public const int MaxResponseSlotsPerAccount = 131_072;
    public const long MaxResponseBytes = 3_145_728; // 3 MiB

    public static readonly RlpLimit GetByteCodesHashesRlpLimit = RlpLimit.For<GetByteCodesMessage>(MaxRequestHashes, nameof(GetByteCodesMessage.Hashes));
    public static readonly RlpLimit GetStorageRangeAccountsRlpLimit = RlpLimit.For<GetStorageRangeMessage>(MaxRequestAccounts, nameof(GetStorageRangeMessage.StorageRange));
    public static readonly RlpLimit GetTrieNodesPathGroupsRlpLimit = RlpLimit.For<GetTrieNodesMessage>(MaxRequestPathGroups, nameof(GetTrieNodesMessage.Paths));
    public static RlpLimit GetTrieNodesPathsPerGroupRlpLimit = RlpLimit.For<PathGroup>(MaxRequestPathsPerGroup, nameof(PathGroup.Group));

    /// <summary>
    /// Raises the per-group paths limit if <paramref name="newLimit"/> exceeds the compiled default.
    /// Call once at startup from configuration. Only increases are applied.
    /// </summary>
    public static void RaisePathsPerGroupLimit(int newLimit)
    {
        if (newLimit > MaxRequestPathsPerGroup)
            GetTrieNodesPathsPerGroupRlpLimit = RlpLimit.For<PathGroup>(newLimit, nameof(PathGroup.Group));
    }

    public static readonly RlpLimit AccountRangeEntriesRlpLimit = RlpLimit.For<AccountRangeMessage>(MaxResponseAccounts, nameof(AccountRangeMessage.PathsWithAccounts));
    public static readonly RlpLimit StorageRangeAccountsRlpLimit = RlpLimit.For<StorageRangeMessage>(MaxRequestAccounts, nameof(StorageRangeMessage.Slots));
    public static readonly RlpLimit StorageRangeSlotsPerAccountRlpLimit = RlpLimit.For<PathWithStorageSlot>(MaxResponseSlotsPerAccount, nameof(StorageRangeMessage.Slots));

    public static long ClampResponseBytes(long requestedBytes) => Math.Clamp(requestedBytes, 1L, MaxResponseBytes);
}
