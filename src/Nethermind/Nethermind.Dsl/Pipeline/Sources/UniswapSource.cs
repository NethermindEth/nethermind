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

#nullable enable
using System;
using System.Linq;
using System.Numerics;
using Nethermind.Abi;
using Nethermind.Api;
using Nethermind.Blockchain.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Dsl.Contracts;
using Nethermind.Pipeline;

namespace Nethermind.Dsl.Pipeline.Sources
{
    public class UniswapSource : IPipelineElement<UniswapData>
    {
        public Action<UniswapData> Emit { private get; set; }
        private readonly IBlockProcessor _blockProcessor;
        private readonly AbiSignature _swapSignatureV3;
        private readonly INethermindApi _api;
        private readonly UniswapV3Factory _v3Factory;
        private readonly UniswapV2Factory _uniswapV2Factory;
        private Address _usdcAddress = new Address("0xA0b86991c6218b36c1d19D4a2e9Eb0cE3606eB48");
        private Address _v3FactoryAddress = new Address("0x1F98431c8aD98523631AE4a59f267346ea31F984");

        public UniswapSource(IBlockProcessor blockProcessor, INethermindApi api)
        {
            _api = api;
            _blockProcessor = blockProcessor;
            _blockProcessor.TransactionProcessed += OnTransactionProcessed;
            _swapSignatureV3 = new AbiSignature("Swap", AbiType.Address, AbiType.Address, AbiType.Int256, AbiType.Int256, AbiType.UInt160, AbiType.UInt128,
                AbiType.Int24);
            _v3Factory = new UniswapV3Factory(_v3FactoryAddress ,_api.CreateBlockchainBridge());
        }

        private void OnTransactionProcessed(object? sender, TxProcessedEventArgs args)
        {
            var logs = args.TxReceipt.Logs;
            
            if(logs is null || !logs.Any()) return;

            var swapLogs = logs.Where(l => l.Topics.Any() && l.Topics.First().Equals(_swapSignatureV3.Hash));

            foreach (var log in swapLogs)
            {
                var data = ConvertLogToData(log);
                data.Transaction = args.Transaction.Hash;
                Emit?.Invoke(data);
            }
        }

        private UniswapData ConvertLogToData(LogEntry log)
        {
            var pool = new UniswapV3Pool(log.LoggersAddress, _api.CreateBlockchainBridge());
            
            return new UniswapData()
            {
                Swapper = new Address(log.Topics[1]),
                Pool = log.LoggersAddress,
                TokenADelta = log.Data.Take(32).ToArray().ToSignedBigInteger(32),
                TokenBDelta = log.Data.Skip(32).Take(32).ToArray().ToSignedBigInteger(32),
                Token0 = pool.token0(_api.BlockTree.Head.Header),
                Token1 = pool.token1(_api.BlockTree.Head.Header)
            };
        }
    }

    public class UniswapData
    {
        public Keccak? Transaction { get; set; }
        public Address? Swapper { get; set; }
        public Address? Pool { get; set; }
        public Address? Token0 { get; set; }
        public Address? Token1 { get; set; }
        public decimal Token0Price { get; set; }
        public decimal Token1Price { get; set; }
        public BigInteger TokenADelta { get; set; }
        public BigInteger TokenBDelta { get; set; }
    }
}