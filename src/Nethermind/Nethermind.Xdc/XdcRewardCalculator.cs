// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Rewards;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Xdc.Contracts;
using Nethermind.Xdc.Spec;
using System;

namespace Nethermind.Xdc;

/// <summary>
/// Wraps <see cref="XdcEpochRewardCalculator"/> for the block-processing path.
/// In addition to reward computation it updates the minted-record contract when
/// the TIP-upgrade reward model is active.
/// </summary>
public class XdcRewardCalculator(
    XdcEpochRewardCalculator epochRewardCalculator,
    ISpecProvider specProvider,
    IMintedRecordContract mintedRecordContract,
    ITransactionProcessor transactionProcessor) : IRewardCalculator
{
    /// <inheritdoc/>
    /// <remarks>
    /// Delegates reward computation to <see cref="XdcEpochRewardCalculator.CalculateEpochRewardsCore"/>,
    /// then calls <see cref="IMintedRecordContract.UpdateAccounting"/> when the TIP-upgrade path
    /// was taken. State mutation must only happen here, inside the block-processing context.
    /// </remarks>
    public BlockReward[] CalculateRewards(Block block)
    {
        ArgumentNullException.ThrowIfNull(block);
        if (block.Header is not XdcBlockHeader xdcHeader)
            throw new InvalidOperationException("Only supports XDC headers");

        (BlockReward[] rewards, UInt256 burnedInEpoch, bool isTipUpgrade) = epochRewardCalculator.CalculateEpochRewardsCore(xdcHeader, transactionProcessor);

        if (isTipUpgrade)
        {
            IXdcReleaseSpec spec = specProvider.GetXdcSpec(xdcHeader, xdcHeader.ExtraConsensusData.BlockRound);
            UInt256 totalMinted = UInt256.Zero;
            foreach (BlockReward reward in rewards)
                totalMinted += reward.Value;

            mintedRecordContract.UpdateAccounting(transactionProcessor, xdcHeader, spec, totalMinted, burnedInEpoch);
        }

        return rewards;
    }
}
