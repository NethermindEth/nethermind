// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Evm.Tracing;

namespace Nethermind.Consensus.Processing
{
    public interface IBlockchainProcessor : IAsyncDisposable
    {
        ITracerBag Tracers { get; }

        void Start();

        Task StopAsync(bool processRemainingBlocks = false);

        Block? Process(Block block, ProcessingOptions options, IBlockTracer tracer, CancellationToken token = default);

        bool IsProcessingBlocks(ulong? maxProcessingInterval);

        event EventHandler<InvalidBlockEventArgs> InvalidBlock;

        public class InvalidBlockEventArgs : EventArgs
        {
            public Block InvalidBlock { get; init; }
        }
    }
}
