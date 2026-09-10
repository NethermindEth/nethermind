// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Consensus.Qbft.Config;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;

namespace Nethermind.Consensus.Qbft.Blocks;

/// <summary>
/// <see cref="HeaderValidator"/> with the BFT timing rules: a block may not be more than one second in
/// the future and must be at least the block period after its parent.
/// </summary>
/// <remarks>
/// The parent-distance rule is skipped when the test-only millisecond block period is in effect,
/// because header timestamps only have second resolution. Extra data size is not limited here; its
/// structure is validated by <see cref="BftSealValidator"/>.
/// </remarks>
public class BftHeaderValidator(
    IBlockTree blockTree,
    ISealValidator sealValidator,
    ISpecProvider specProvider,
    BftForksSchedule forksSchedule,
    ITimestamper timestamper,
    ILogManager logManager) : HeaderValidator(blockTree, sealValidator, specProvider, logManager)
{
    /// <summary>Besu's <c>TimestampBoundedByFutureParameter(1)</c>: acceptable clock drift into the future.</summary>
    public const ulong AcceptableFutureDriftSeconds = 1;

    protected override bool ValidateExtraData(BlockHeader header, IReleaseSpec spec, bool isUncle, ref string? error) => true;

    protected override bool ValidateTimestamp(BlockHeader header, BlockHeader parent, ref string? error)
    {
        if (parent is null) return true;

        ulong now = timestamper.UnixTime.Seconds;
        if (header.Timestamp > now + AcceptableFutureDriftSeconds)
        {
            error = $"Invalid block header ({header.Hash}) - timestamp {header.Timestamp} is more than {AcceptableFutureDriftSeconds}s in the future (now {now})";
            if (_logger.IsWarn) _logger.Warn(error);
            return false;
        }

        BftConfigSnapshot config = forksSchedule.GetFork((long)header.Number, header.Timestamp);
        TimeSpan minimumPeriod = config.MinimumBlockPeriod;
        if (minimumPeriod < TimeSpan.FromSeconds(1))
        {
            return true;
        }

        ulong minimumTimestamp = parent.Timestamp + (ulong)minimumPeriod.TotalSeconds;
        if (header.Timestamp < minimumTimestamp)
        {
            error = $"Invalid block header ({header.Hash}) - timestamp {header.Timestamp} is less than {minimumPeriod.TotalSeconds}s after parent {parent.Timestamp}";
            if (_logger.IsWarn) _logger.Warn(error);
            return false;
        }

        return true;
    }
}
