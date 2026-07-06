// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Rewards;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Xdc.Contracts;

namespace Nethermind.Xdc;

internal class XdcRewardCalculatorSource(
    XdcEpochRewardCalculator epochRewardCalculator,
    ISpecProvider specProvider,
    IMintedRecordContract mintedRecordContract) : IRewardCalculatorSource
{
    protected XdcEpochRewardCalculator EpochRewardCalculator { get; } = epochRewardCalculator;
    protected ISpecProvider SpecProvider { get; } = specProvider;
    protected IMintedRecordContract MintedRecordContract { get; } = mintedRecordContract;

    public virtual IRewardCalculator Get(ITransactionProcessor processor) =>
        new XdcRewardCalculator(EpochRewardCalculator, SpecProvider, MintedRecordContract, processor);
}
