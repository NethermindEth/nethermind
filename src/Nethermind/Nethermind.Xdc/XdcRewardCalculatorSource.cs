// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
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
    IComponentContext context) : IRewardCalculatorSource
{
    public virtual IRewardCalculator Get(ITransactionProcessor processor) => new XdcRewardCalculator(
        epochSwitchManager,
        specProvider,
        blockTree,
        masternodeVotingContract,
        context.Resolve<IMintedRecordContract>(),
        signingTxCache,
        processor,
        context.Resolve<IRewardsStore>(),
        context.Resolve<IRewardMasternodeSelector>());
}
