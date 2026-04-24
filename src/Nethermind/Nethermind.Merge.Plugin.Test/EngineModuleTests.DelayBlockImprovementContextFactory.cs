// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Threading;
using Nethermind.Int256;

namespace Nethermind.Merge.Plugin.Test;

public partial class EngineModuleTests
{
    private class DelayBlockImprovementContextFactory(IBlockProducer blockProducer, TimeSpan timeout, TimeSpan delay)
        : IBlockImprovementContextFactory
    {
        public IBlockImprovementContext StartBlockImprovementContext(Block currentBestBlock, BlockHeader parentHeader, PayloadAttributes payloadAttributes, DateTimeOffset startDateTime,
        UInt256 currentBlockFees, SharedCancellationTokenSource cts) =>
            new DelayBlockImprovementContext(currentBestBlock, blockProducer, timeout, parentHeader, payloadAttributes, delay, startDateTime, cts);
    }

    /// <summary>
    /// Only the first improvement context builds a block (with zero delay).
    /// Subsequent contexts block indefinitely until cancelled, making tests
    /// deterministic without artificial time delays.
    /// </summary>
    private class FirstOnlyBlockImprovementContextFactory(IBlockProducer blockProducer, TimeSpan timeout) : IBlockImprovementContextFactory
    {
        private int _callCount;

        public IBlockImprovementContext StartBlockImprovementContext(
            Block currentBestBlock, BlockHeader parentHeader, PayloadAttributes payloadAttributes,
            DateTimeOffset startDateTime, UInt256 currentBlockFees, SharedCancellationTokenSource cts)
        {
            TimeSpan delay = Interlocked.Increment(ref _callCount) == 1
                ? TimeSpan.Zero
                : Timeout.InfiniteTimeSpan;
            return new DelayBlockImprovementContext(
                currentBestBlock, blockProducer, timeout, parentHeader, payloadAttributes,
                delay, startDateTime, cts);
        }
    }

    private class DelayBlockImprovementContext : IBlockImprovementContext
    {
        private readonly SharedCancellationTokenSource _improvementCancellation;
        private CancellationTokenSource? _timeOutCancellation;
        private CancellationTokenSource? _linkedCancellation;

        public DelayBlockImprovementContext(Block currentBestBlock,
            IBlockProducer blockProducer,
            TimeSpan timeout,
            BlockHeader parentHeader,
            PayloadAttributes payloadAttributes,
            TimeSpan delay,
            DateTimeOffset startDateTime,
            SharedCancellationTokenSource cts)
        {
            CurrentBestBlock = currentBestBlock;
            StartDateTime = startDateTime;
            _improvementCancellation = cts;
            _timeOutCancellation = new CancellationTokenSource(timeout);
            _linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, _timeOutCancellation.Token);
            ImprovementTask = BuildBlock(blockProducer, parentHeader, payloadAttributes, delay, _linkedCancellation.Token);
        }

        private async Task<Block?> BuildBlock(
            IBlockProducer blockProducer,
            BlockHeader parentHeader,
            PayloadAttributes payloadAttributes,
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            Block? block = await blockProducer.BuildBlock(parentHeader, NullBlockTracer.Instance, payloadAttributes, IBlockProducer.Flags.None, cancellationToken);
            if (block is not null)
            {
                CurrentBestBlock = block;
            }

            return CurrentBestBlock;
        }

        public Task<Block?> ImprovementTask { get; }
        public Block? CurrentBestBlock { get; private set; }
        public UInt256 BlockFees { get; }
        public bool Disposed { get; private set; }
        public DateTimeOffset StartDateTime { get; }

        public void CancelOngoingImprovements() => _improvementCancellation.CancelAndDispose();

        public void Dispose()
        {
            Disposed = true;
            CancellationTokenExtensions.CancelDisposeAndClear(ref _linkedCancellation);
            CancellationTokenExtensions.CancelDisposeAndClear(ref _timeOutCancellation);
        }
    }
}
