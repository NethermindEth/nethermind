// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Evm.Tracing;

namespace Nethermind.Consensus.Producers;

/// <summary>Raises <see cref="IBlockBuildingTracker.IsBuildingBlock"/> while a block producer environment executes a block,
/// so gossiped frame-transaction validation yields to the build as it yields to block import.</summary>
public sealed class BlockBuildingTrackingProcessor(IBlockchainProcessor processor, IBlockBuildingTracker tracker) : IBlockchainProcessor
{
    public Block? Process(Block block, ProcessingOptions options, IBlockTracer tracer, CancellationToken token = default)
    {
        using IDisposable building = tracker.BeginBlockBuilding();
        return processor.Process(block, options, tracer, token);
    }

    // The container owns the decorated processor and disposes it.
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
