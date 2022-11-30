// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing;

namespace Nethermind.Consensus.Processing
{
    public interface IBlockchainProcessor : IDisposable
    {
        ITracerBag Tracers { get; }

        void Start();

        Task StopAsync(bool processRemainingBlocks = false);

        Block? Process(Block block, ProcessingOptions options, IBlockTracer tracer);

        bool IsProcessingBlocks(ulong? maxProcessingInterval);

        event EventHandler<InvalidBlockEventArgs> InvalidBlock;

        public class InvalidBlockEventArgs : EventArgs
        {
            public Block InvalidBlock { get; init; }
        }
    }
}
