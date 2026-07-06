// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Rewards;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Xdc.Contracts;

namespace Nethermind.Xdc;

internal sealed class XdcSubnetRewardCalculatorSource(
    XdcSubnetRewardCalculator subnetEpochCalculator,
    ISpecProvider specProvider,
    IMintedRecordContract mintedRecordContract)
    : XdcRewardCalculatorSource(subnetEpochCalculator, specProvider, mintedRecordContract)
{
    public override IRewardCalculator Get(ITransactionProcessor processor) =>
        new XdcRewardCalculator(EpochRewardCalculator, SpecProvider, MintedRecordContract, processor);
}
