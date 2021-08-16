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
// 

using System;
using System.Threading;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Facade;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Mev.Data;
using Nethermind.Specs;
using Nethermind.Specs.Forks;

namespace Nethermind.Mev.Execution
{
    public abstract class TxBundleExecutor<TResult, TBlockTracer> where TBlockTracer : IBlockTracer
    {
        private readonly ITracerFactory _tracerFactory;
        private readonly ISpecProvider _specProvider;
        private readonly ISigner? _signer;

        protected TxBundleExecutor(ITracerFactory tracerFactory, ISpecProvider specProvider, ISigner? signer)
        {
            _tracerFactory = tracerFactory;
            _specProvider = specProvider;
            _signer = signer;
        }
            
        public TResult ExecuteBundle(MevBundle bundle, BlockHeader parent, CancellationToken cancellationToken, UInt256? timestamp = null)
        {
            Block block = BuildBlock(bundle, parent, timestamp);
            TBlockTracer blockTracer = CreateBlockTracer(bundle);
            ITracer tracer = _tracerFactory.Create();
            tracer.Trace(block, blockTracer.WithCancellation(cancellationToken));
            return BuildResult(bundle, blockTracer);
        }

        protected abstract TResult BuildResult(MevBundle bundle, TBlockTracer tracer);

        private Block BuildBlock(MevBundle bundle, BlockHeader parent, UInt256? timestamp)
        {
            BlockHeader header = new(
                parent.Hash ?? Keccak.OfAnEmptySequenceRlp, 
                Keccak.OfAnEmptySequenceRlp, 
                Beneficiary, 
                parent.Difficulty,  
                parent.Number + 1, 
                GetGasLimit(parent), 
                timestamp ?? parent.Timestamp, 
                Bytes.Empty)
            {
                TotalDifficulty = parent.TotalDifficulty + parent.Difficulty
            };

            header.BaseFeePerGas = BaseFeeCalculator.Calculate(parent, _specProvider.GetSpec(header.Number));

            return new Block(header, bundle.Transactions, Array.Empty<BlockHeader>());
        }

        protected virtual long GetGasLimit(BlockHeader parent) => parent.GasLimit;

        protected Address Beneficiary => _signer?.Address ?? Address.Zero;

        protected abstract TBlockTracer CreateBlockTracer(MevBundle mevBundle);

        protected ResultWrapper<TResult> GetInputError(BlockchainBridge.CallOutput result) => 
            ResultWrapper<TResult>.Fail(result.Error ?? "", ErrorCodes.InvalidInput);
            

    }
}
