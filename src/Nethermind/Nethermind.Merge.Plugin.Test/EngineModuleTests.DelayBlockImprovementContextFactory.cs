// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.Merge.Plugin.Test;

public partial class EngineModuleTests
{
    private class DelayBlockImprovementContextFactory : IBlockImprovementContextFactory
    {
        private readonly IBlockProducer _blockProducer;
        private readonly TimeSpan _timeout;
        private readonly TimeSpan _delay;

        public DelayBlockImprovementContextFactory(IBlockProducer blockProducer, TimeSpan timeout, TimeSpan delay)
        {
            _blockProducer = blockProducer;
            _timeout = timeout;
            _delay = delay;
        }

        public IBlockImprovementContext StartBlockImprovementContext(Block currentBestBlock, BlockHeader parentHeader, PayloadAttributes payloadAttributes, DateTimeOffset startDateTime,
        UInt256 currentBlockFees, CancellationTokenSource cts) =>
            new DelayBlockImprovementContext(currentBestBlock, _blockProducer, _timeout, parentHeader, payloadAttributes, _delay, startDateTime, cts);
    }

    private class DelayBlockImprovementContext : IBlockImprovementContext
    {
        private readonly CancellationTokenSource _improvementCancellation;
        private CancellationTokenSource? _timeOutCancellation;
        private CancellationTokenSource? _linkedCancellation;

        public DelayBlockImprovementContext(Block currentBestBlock,
            IBlockProducer blockProducer,
            TimeSpan timeout,
            BlockHeader parentHeader,
            PayloadAttributes payloadAttributes,
            TimeSpan delay,
            DateTimeOffset startDateTime,
            CancellationTokenSource cts)
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
            Block? block = await blockProducer.BuildBlock(parentHeader, NullBlockTracer.Instance, payloadAttributes, cancellationToken);
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

        public void CancelOngoingImprovements() => _improvementCancellation.Cancel();

        public void Dispose()
        {
            Disposed = true;
            CancellationTokenExtensions.CancelDisposeAndClear(ref _linkedCancellation);
            CancellationTokenExtensions.CancelDisposeAndClear(ref _timeOutCancellation);
        }
    }
}
