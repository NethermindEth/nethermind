// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;

namespace Nethermind.Consensus.Stateless;

public interface IWitnessCollector
{
    Witness GetWitness(BlockHeader parentHeader, Block block);
}

public class WitnessCollector(WitnessGeneratingBlockFinder blockFinder, WitnessGeneratingWorldState worldState, IBlockProcessor blockProcessor, ISpecProvider specProvider) : IWitnessCollector
{
    public Witness GetWitness(BlockHeader parentHeader, Block block)
    {
        blockProcessor.ProcessOne(block, ProcessingOptions.DoNotUpdateHead & ProcessingOptions.ReadOnlyChain,
            NullBlockTracer.Instance, specProvider.GetSpec(block.Header));
        (byte[][] stateNodes, byte[][] codes, byte[][] keys) = worldState.GetStateWitness(parentHeader.StateRoot);
        return new Witness()
        {
            Headers = blockFinder.GetWitnessHeaders(parentHeader.Hash),
            Codes = codes,
            State = stateNodes,
            Keys = keys
        };
    }
}
