// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Eez.Execution;

/// <summary>The finalized L1 block a RollupX genesis is pinned to: it fixes the slot phase and the first prevRandao seed.</summary>
public sealed record RollupXL1Reference(ulong ChainId, ulong Number, Hash256 Hash, ulong Timestamp, Hash256 PrevRandao);

/// <summary>
/// RollupX's block grid: block <c>n</c> has timestamp <c>genesis + n * BlockIntervalSeconds</c>, and every
/// <see cref="SyncIntervalBlocks"/>-th block is a Sync position, one per L1 slot of <see cref="L1SlotSeconds"/>.
/// </summary>
public sealed record RollupXSchedule(ulong BlockIntervalSeconds, uint SyncIntervalBlocks, RollupXL1Reference L1Reference, ulong GenesisTimestamp)
{
    public const ulong EthereumChainId = 1;
    public const ulong ChiadoChainId = 10200;

    public ulong L1SlotSeconds => BlockIntervalSeconds * SyncIntervalBlocks;

    /// <summary>The only timestamp block <paramref name="number"/> may carry; <c>null</c> if it overflows.</summary>
    public ulong? Timestamp(ulong number) =>
        number > (ulong.MaxValue - GenesisTimestamp) / BlockIntervalSeconds ? null : GenesisTimestamp + number * BlockIntervalSeconds;

    public bool IsSyncPosition(ulong number) => number % SyncIntervalBlocks == 0;

    /// <summary>
    /// How a settlement in an L1 block at <paramref name="l1Timestamp"/> ending at block <paramref name="number"/> anchors
    /// the chain: live when the endpoint is the L1 block's own slot, catching up when it is earlier.
    /// </summary>
    /// <returns><see langword="null"/> when the endpoint is no Sync position, lies after the L1 block, or the L1 block is off the reference's slots.</returns>
    public RollupXAnchorKind? AnchorKind(ulong number, ulong l1Timestamp)
    {
        if (!IsSyncPosition(number) || Timestamp(number) is not { } timestamp || l1Timestamp < L1Reference.Timestamp
            || (l1Timestamp - L1Reference.Timestamp) % L1SlotSeconds != 0 || timestamp > l1Timestamp)
        {
            return null;
        }

        return timestamp == l1Timestamp ? RollupXAnchorKind.Live : RollupXAnchorKind.CatchUp;
    }
}

/// <summary>
/// A live settlement refreshes the prevRandao of the blocks after it with its L1 block's; a catching-up one keeps the
/// seed in effect and may carry no cross-chain actions.
/// </summary>
public enum RollupXAnchorKind
{
    Live,
    CatchUp,
}
