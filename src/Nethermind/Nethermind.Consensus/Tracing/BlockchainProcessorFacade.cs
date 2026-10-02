// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using Nethermind.Blockchain.Tracing;
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
    IReadOnlyList<IBlockPreprocessorStep> preprocessorSteps
)
{
    public Block? Process(Block block, ProcessingOptions options, IBlockTracer tracer, CancellationToken token = default)
    {
        for (int i = 0; i < preprocessorSteps.Count; i++)
        {
            preprocessorSteps[i].RecoverData(block);
        }

        IReleaseSpec spec = specProvider.GetSpec(block.Header);

        // Mirror BranchProcessor: a traced block runs the sequential EIP-7928 BAL path. This facade bypasses
        // BranchProcessor, so it also bypasses its parallel BlockAccessListSequentialRetryException handler —
        // forcing sequential here keeps traced processing off the unhandled parallel path.
        ProcessingOptions blockOptions = tracer == NullBlockTracer.Instance
            ? options
            : options | ProcessingOptions.ForceSequentialBlockAccessList;

        (Block? processedBlock, TxReceipt[] _) = blockProcessor.ProcessOne(block, blockOptions, tracer, spec, token);
        return processedBlock;
    }
}
