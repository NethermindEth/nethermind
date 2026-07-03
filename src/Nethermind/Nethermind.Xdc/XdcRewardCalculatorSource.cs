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
    IMintedRecordContract mintedRecordContract,
    ISigningTxCache signingTxCache,
    IRewardsStore rewardsStore) : IRewardCalculatorSource
{
    protected IEpochSwitchManager EpochSwitchManager { get; } = epochSwitchManager;
    protected ISpecProvider SpecProvider { get; } = specProvider;
    protected IBlockTree BlockTree { get; } = blockTree;
    protected IMasternodeVotingContract MasternodeVotingContract { get; } = masternodeVotingContract;
    protected IMintedRecordContract MintedRecordContract { get; } = mintedRecordContract;
    protected ISigningTxCache SigningTxCache { get; } = signingTxCache;
    protected IRewardsStore RewardsStore { get; } = rewardsStore;

    public virtual IRewardCalculator Get(ITransactionProcessor processor) => new XdcRewardCalculator(
        EpochSwitchManager,
        SpecProvider,
        BlockTree,
        MasternodeVotingContract,
        MintedRecordContract,
        SigningTxCache,
        processor,
        RewardsStore);
}
