// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;

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

    internal WitnessCollector(WitnessGeneratingBlockFinder blockFinder, WitnessGeneratingWorldState worldState, IBlockProcessor blockProcessor)
    {
        _worldState = worldState;
        _blockFinder = blockFinder;
        _blockProcessor = blockProcessor;
    }

    public Witness GetWitness(BlockHeader parentHeader, Block block)
    {
        _blockProcessor.Process(parentHeader, [block],
            ProcessingOptions.DoNotUpdateHead & ProcessingOptions.ReadOnlyChain, NullBlockTracer.Instance);
        (byte[][] stateNodes, byte[][] codes) = _worldState.GetStateWitness(parentHeader.StateRoot);
        return new Witness()
        {
            Headers = _blockFinder.GetWitnessHeaders(parentHeader.Hash),
            Codes = codes,
            State = stateNodes,
        };
    }
}
