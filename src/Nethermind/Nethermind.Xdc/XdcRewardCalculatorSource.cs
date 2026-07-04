// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Consensus.Rewards;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Xdc.Contracts;

namespace Nethermind.Xdc;

internal class XdcRewardCalculatorSource(
    IEpochSwitchManager epochSwitchManager,
    ISpecProvider specProvider,
    IBlockTree blockTree,
    IMasternodeVotingContract masternodeVotingContract,
    ISigningTxCache signingTxCache,
    IRewardMasternodeSelector rewardMasternodeSelector) : IRewardCalculatorSource
{
    public virtual IRewardCalculator Get(ITransactionProcessor processor) => new XdcRewardCalculator(
        epochSwitchManager,
        specProvider,
        blockTree,
        masternodeVotingContract,
        signingTxCache,
        processor,
        rewardMasternodeSelector);
}
