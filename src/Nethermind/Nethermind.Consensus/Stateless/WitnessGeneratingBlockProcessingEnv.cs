// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Processing;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.Consensus.Stateless;

public interface IWitnessGeneratingBlockProcessingEnv
{
    IExistingBlockWitnessCollector CreateExistingBlockWitnessCollector();
    ISingleCallWitnessCollector CreateSingleCallWitnessCollector();
}

public class WitnessGeneratingBlockProcessingEnv(
    IWorldState worldState,
    AccessWitnessScopeProvider accessWitness,
    WitnessGeneratingHeaderFinder headerFinder,
    IBlockProcessor blockProcessor,
    ITransactionProcessor transactionProcessor,
    ISpecProvider specProvider) : IWitnessGeneratingBlockProcessingEnv
{
    public IExistingBlockWitnessCollector CreateExistingBlockWitnessCollector()
        => new WitnessCollector(worldState, accessWitness, headerFinder, blockProcessor, specProvider);

    public ISingleCallWitnessCollector CreateSingleCallWitnessCollector()
        => new SingleCallWitnessCollector(worldState, accessWitness, headerFinder, transactionProcessor);
}
