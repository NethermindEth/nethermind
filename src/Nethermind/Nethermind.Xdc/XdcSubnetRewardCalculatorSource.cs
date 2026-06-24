// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Consensus.Rewards;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Xdc.Contracts;

namespace Nethermind.Xdc;

internal sealed class XdcSubnetRewardCalculatorSource(
    IEpochSwitchManager epochSwitchManager,
    ISpecProvider specProvider,
    IBlockTree blockTree,
    IMasternodeVotingContract masternodeVotingContract,
    IMintedRecordContract mintedRecordContract,
    ISigningTxCache signingTxCache)
    : XdcRewardCalculatorSource(
        epochSwitchManager,
        specProvider,
        blockTree,
        masternodeVotingContract,
        mintedRecordContract,
        signingTxCache)
{
    public override IRewardCalculator Get(ITransactionProcessor processor) => new XdcSubnetRewardCalculator(
        EpochSwitchManager,
        SpecProvider,
        BlockTree,
        MasternodeVotingContract,
        MintedRecordContract,
        SigningTxCache,
        processor);
}
