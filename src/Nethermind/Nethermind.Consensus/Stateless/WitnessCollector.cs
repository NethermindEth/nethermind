// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Consensus.Stateless;

public interface IExistingBlockWitnessCollector
{
    Witness GetWitnessForExistingBlock(BlockHeader parentHeader, Block block);
}

public class WitnessCollector(
    WitnessGeneratingHeaderFinder headerFinder,
    WitnessGeneratingWorldState worldState,
    WitnessCapturingTrieStore trieStore,
    IBlockProcessor blockProcessor,
    ISpecProvider specProvider) : IExistingBlockWitnessCollector
{
    public Witness GetWitnessForExistingBlock(BlockHeader parentHeader, Block block)
    {
        using (worldState.BeginScope(parentHeader))
        {
            (Block processed, TxReceipt[] receipts) = blockProcessor.ProcessOne(block, ProcessingOptions.ReadOnlyChain,
                NullBlockTracer.Instance, specProvider.GetSpec(block.Header));

            (byte[][] stateNodes, byte[][] codes, byte[][] keys) = worldState.GetWitness(parentHeader, trieStore.TouchedNodesRlp);

            return new Witness()
            {
                Headers = headerFinder.GetWitnessHeaders(parentHeader.Hash),
                Codes = codes,
                State = stateNodes,
                Keys = keys
            };
        }
    }
}
