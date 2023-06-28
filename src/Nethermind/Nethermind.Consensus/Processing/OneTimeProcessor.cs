// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Evm.Tracing;

namespace Nethermind.Consensus.Processing
{
    public class OneTimeChainProcessor : IBlockchainProcessor
    {
        public ITracerBag Tracers => _processor.Tracers;

        protected readonly IBlockchainProcessor _processor;
        protected readonly IReadOnlyDbProvider _readOnlyDbProvider;

        protected object _lock = new();

        public OneTimeChainProcessor(IReadOnlyDbProvider readOnlyDbProvider, IBlockchainProcessor processor)
        {
            _readOnlyDbProvider = readOnlyDbProvider ?? throw new ArgumentNullException(nameof(readOnlyDbProvider));
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

        public virtual Block? Process(Block block, ProcessingOptions options, IBlockTracer tracer)
        {
            lock (_lock)
            {
                Block result;
                try
                {
                    result = _processor.Process(block, options, tracer);
                }
                finally
                {
                    _readOnlyDbProvider.ClearTempChanges();
                }

                return result;
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

        public virtual void Dispose()
        {
            _processor?.Dispose();
            _readOnlyDbProvider?.Dispose();
        }
    }
}
