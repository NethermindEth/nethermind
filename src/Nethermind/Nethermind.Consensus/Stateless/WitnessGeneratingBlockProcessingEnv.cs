// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Processing;
using Nethermind.Core.Specs;

namespace Nethermind.Consensus.Stateless;

public interface IWitnessGeneratingBlockProcessingEnv
{
    IExistingBlockWitnessCollector CreateExistingBlockWitnessCollector();
}

public class WitnessGeneratingBlockProcessingEnv(
    WitnessGeneratingWorldState witnessWorldState,
    IBlockProcessor blockProcessor,
    ISpecProvider specProvider) : IWitnessGeneratingBlockProcessingEnv
{
    public IExistingBlockWitnessCollector CreateExistingBlockWitnessCollector()
        => new WitnessCollector(witnessWorldState, blockProcessor, specProvider);
}
