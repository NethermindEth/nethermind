// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;

namespace Nethermind.Consensus.Tracing;

/// <summary>
/// A class to make moving from <see cref="IBlockchainProcessor"/> to <see cref="IBlockProcessor"/> simple.
/// It has same interface, but it does not change the current <see cref="IWorldState"/> which we don't want when
/// we are already managing the worldstate from outside.
/// </summary>
public sealed class BlockchainProcessorFacade(
    IBlockProcessor blockProcessor,
    ISpecProvider specProvider,
    CompositeBlockPreprocessorStep preprocessorStep
)
{
    public Block? Process(Block block, ProcessingOptions options, IBlockTracer tracer, CancellationToken token = default, string? forkName = null)
    {
        preprocessorStep.RecoverData(block);

        IReleaseSpec spec = specProvider.GetSpec(block.Header);
        try
        {
            (Block? processedBlock, TxReceipt[] _) = blockProcessor.ProcessOne(block, options, tracer, spec, token, forkName: forkName);
            return processedBlock;
        }
        catch (InvalidBlockException)
        {
            return null;
        }
    }
}
