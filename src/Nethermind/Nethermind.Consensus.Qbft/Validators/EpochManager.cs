// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Consensus.Qbft.Validators;

/// <summary>Epoch arithmetic: an epoch block resets outstanding votes and restates the full validator list.</summary>
/// <param name="epochLength">Blocks per epoch.</param>
/// <param name="startBlock">First block of the consensus era (non-zero only for a chain migrated from IBFT 2.0).</param>
/// <param name="ibft2EpochLength">
/// Epoch length of the IBFT 2.0 era below <paramref name="startBlock"/>. Besu answers those heights from a separate
/// IBFT 2.0 context; here one manager covers both eras so historical blocks validate during sync.
/// </param>
public sealed class EpochManager(long epochLength, long startBlock = 0, long? ibft2EpochLength = null)
{
    public long EpochLength { get; } = epochLength > 0 ? epochLength : throw new ArgumentOutOfRangeException(nameof(epochLength), "Epoch length in config must be greater than zero");
    public long StartBlock { get; } = startBlock;
    public long Ibft2EpochLength { get; } = ibft2EpochLength is null or > 0 ? ibft2EpochLength ?? epochLength : throw new ArgumentOutOfRangeException(nameof(ibft2EpochLength), "Epoch length in config must be greater than zero");

    public bool IsEpochBlock(long blockNumber)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(blockNumber);
        if (blockNumber < StartBlock - 1)
        {
            return blockNumber % Ibft2EpochLength == 0;
        }

        // The last IBFT 2.0 block seeds the first QBFT validator set, so it counts as the first QBFT epoch block.
        return (blockNumber - (StartBlock == 0 ? 0 : StartBlock - 1)) % EpochLength == 0;
    }

    public long GetLastEpochBlock(long blockNumber)
    {
        if (blockNumber < StartBlock)
        {
            return blockNumber - blockNumber % Ibft2EpochLength;
        }

        return StartBlock + (blockNumber - StartBlock) / EpochLength * EpochLength;
    }
}
