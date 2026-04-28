// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Processing;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Consensus.Stateless;

public interface IWitnessGeneratingBlockProcessingEnv
{
    IExistingBlockWitnessCollector CreateExistingBlockWitnessCollector();
    ISingleCallWitnessCollector CreateSingleCallWitnessCollector();
}

public class WitnessGeneratingBlockProcessingEnv(
    WitnessGeneratingWorldState witnessWorldState,
    IBlockProcessor blockProcessor,
    ITransactionProcessor transactionProcessor,
    ISpecProvider specProvider) : IWitnessGeneratingBlockProcessingEnv
{
    public IExistingBlockWitnessCollector CreateExistingBlockWitnessCollector()
        => new WitnessCollector(witnessWorldState, blockProcessor, specProvider);

    public ISingleCallWitnessCollector CreateSingleCallWitnessCollector()
        => new SingleCallWitnessCollector(witnessWorldState, transactionProcessor);
}
