// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Evm.Tracing;
using Nethermind.State;

namespace Nethermind.Consensus.Processing
{
    public sealed class OneTimeChainProcessor : IBlockchainProcessor
    {
        public ITracerBag Tracers => _processor.Tracers;

        private readonly IBlockchainProcessor _processor;
        private readonly IWorldState _worldState;

        private readonly Lock _lock = new();

        public OneTimeChainProcessor(IWorldState worldState, IBlockchainProcessor processor)
        {
            _worldState = worldState ?? throw new ArgumentNullException(nameof(worldState));
            _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        }

        public void Start()
        {
            _processor.Start();
        }

        public Task StopAsync(bool processRemainingBlocks = false)
        {
            return _processor.StopAsync(processRemainingBlocks);
        }

        public Block? Process(Block block, ProcessingOptions options, IBlockTracer tracer, CancellationToken token)
        {
            lock (_lock)
            {
                return _processor.Process(block, options, tracer, token);
            }
        }

        public bool IsProcessingBlocks(ulong? maxProcessingInterval)
        {
            return _processor.IsProcessingBlocks(maxProcessingInterval);
        }

#pragma warning disable 67
        public event EventHandler<BlockProcessedEventArgs> BlockProcessed;
        public event EventHandler<BlockProcessedEventArgs> BlockInvalid;
        public event EventHandler<IBlockchainProcessor.InvalidBlockEventArgs>? InvalidBlock;
#pragma warning restore 67

        public ValueTask DisposeAsync() => _processor?.DisposeAsync() ?? default;
    }
}
