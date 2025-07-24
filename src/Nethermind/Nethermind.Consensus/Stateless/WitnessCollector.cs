// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.Consensus.Stateless;

public interface IWitnessCollector
{
    Witness GetWitness(BlockHeader parentHeader, Block block);
}


public class WitnessCollector : IWitnessCollector
{
    private WitnessGeneratingWorldState _worldState;
    private WitnessGeneratingBlockFinder _blockFinder;
    private IBlockProcessor _blockProcessor;
    private ILogger _logger;

    internal WitnessCollector(WitnessGeneratingBlockFinder blockFinder, WitnessGeneratingWorldState worldState, IBlockProcessor blockProcessor, ILogger logger)
    {
        _worldState = worldState;
        _blockFinder = blockFinder;
        _blockProcessor = blockProcessor;
        _logger = logger;
    }

    public Witness GetWitness(BlockHeader parentHeader, Block block)
    {
        _blockProcessor.Process(parentHeader, [block],
            ProcessingOptions.DoNotUpdateHead & ProcessingOptions.ReadOnlyChain, NullBlockTracer.Instance);
        (byte[][] stateNodes, byte[][] codes, byte[][] keys) = _worldState.GetStateWitness(parentHeader.StateRoot);
        return new Witness()
        {
            Headers = _blockFinder.GetWitnessHeaders(parentHeader.Hash),
            Codes = codes,
            State = stateNodes,
            Keys = keys
        };
    }
}
