// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Abi;
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
    IMasternodeVotingContract masternodeVotingContract) : IRewardCalculatorSource
{
    public IRewardCalculator Get(ITransactionProcessor processor)
    {
        return new XdcRewardCalculator(epochSwitchManager, specProvider, blockTree, masternodeVotingContract, processor);
    }
}
