// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Consensus.Qbft.Validators;

/// <summary>Epoch arithmetic: an epoch block resets outstanding votes and restates the full validator list.</summary>
/// <param name="epochLength">Blocks per epoch.</param>
/// <param name="startBlock">First block of the consensus era (non-zero only for a chain migrated from IBFT 2.0).</param>
public sealed class EpochManager(long epochLength, long startBlock = 0)
{
    public long EpochLength { get; } = epochLength > 0 ? epochLength : throw new ArgumentOutOfRangeException(nameof(epochLength), "Epoch length in config must be greater than zero");
    public long StartBlock { get; } = startBlock;

    public bool IsEpochBlock(long blockNumber)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(blockNumber);
        if (blockNumber < StartBlock - 1)
        {
            return false;
        }

        return (blockNumber - (StartBlock == 0 ? 0 : StartBlock - 1)) % EpochLength == 0;
    }

    public long GetLastEpochBlock(long blockNumber)
    {
        if (blockNumber < StartBlock)
        {
            throw new ArgumentException("Block number is before start block.", nameof(blockNumber));
        }

        return StartBlock + (blockNumber - StartBlock) / EpochLength * EpochLength;
    }
}
