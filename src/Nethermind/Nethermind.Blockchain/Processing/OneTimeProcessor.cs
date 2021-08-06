//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Evm.Tracing;

namespace Nethermind.Blockchain.Processing
{
    public class OneTimeChainProcessor : IBlockchainProcessor
    {
        public ITracerBag Tracers => _processor.Tracers;

        private readonly IBlockchainProcessor _processor;
        private readonly IReadOnlyDbProvider _readOnlyDbProvider;

        private object _lock = new();

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

        public Block Process(Block block, ProcessingOptions options, IBlockTracer tracer)
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

        public void Dispose()
        {
            _processor?.Dispose();
            _readOnlyDbProvider?.Dispose();
        }
    }
}
