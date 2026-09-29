// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Stateless;
using Nethermind.Core;

namespace Nethermind.Eez.Proving;

/// <summary>The witnesses the sequencer proves its blocks with.</summary>
public interface IWitnessRecorder
{
    /// <summary>Records the witness of a block the sequencer just committed.</summary>
    void Capture(BlockHeader parent, Block block);

    /// <returns>The stored witness, or one generated now when it was not captured; the caller disposes it.</returns>
    Witness Get(BlockHeader parent, Block block);
}

/// <summary>
/// Captures each block's witness when it is committed, so proving never depends on the state of past blocks still
/// being available, and generates one on demand for a block that was not captured, such as after a restart.
/// </summary>
public sealed class WitnessRecorder(IWitnessGeneratingBlockProcessingEnvFactory envFactory, IWitnessStore store) : IWitnessRecorder
{
    public void Capture(BlockHeader parent, Block block)
    {
        using Witness witness = Generate(parent, block);
        store.Put(block.Header, witness);
    }

    public Witness Get(BlockHeader parent, Block block) => store.Find(block.Number, block.Hash!) ?? Generate(parent, block);

    private Witness Generate(BlockHeader parent, Block block)
    {
        using IWitnessGeneratingBlockProcessingEnvScope scope = envFactory.CreateScope();
        return scope.Env.CreateExistingBlockWitnessCollector().GetWitnessForExistingBlock(parent, block);
    }
}
