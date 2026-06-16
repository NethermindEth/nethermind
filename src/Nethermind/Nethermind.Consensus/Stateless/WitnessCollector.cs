// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;

namespace Nethermind.Consensus.Stateless;

public interface IExistingBlockWitnessCollector
{
    Witness GetWitnessForExistingBlock(BlockHeader parentHeader, Block block);
}

public class WitnessCollector(
    IWorldState worldState,
    WitnessGeneratingHeaderFinder headerFinder,
    IBlockProcessor blockProcessor,
    ISpecProvider specProvider) : IExistingBlockWitnessCollector
{
    public Witness GetWitnessForExistingBlock(BlockHeader parentHeader, Block block)
    {
        using IDisposable? scope = worldState.BeginScope(parentHeader, trackWitness: true);
        blockProcessor.ProcessOne(block, ProcessingOptions.ReadOnlyChain, NullBlockTracer.Instance, specProvider.GetSpec(block.Header));
        ScopeWitness scopeWitness = worldState.Witness ?? throw new InvalidOperationException("Witness tracking was not enabled for this scope.");
        return WitnessAssembler.Build(scopeWitness, headerFinder, parentHeader);
    }
}
