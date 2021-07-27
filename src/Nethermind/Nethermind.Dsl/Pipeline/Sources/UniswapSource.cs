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
using Nethermind.Int256;
using Nethermind.Pipeline;

namespace Nethermind.Dsl.Pipeline.Sources
{
    public class UniswapSource : IPipelineElement<UniswapData>
    {
        public Action<UniswapData> Emit { private get; set; }
        private readonly IBlockProcessor _blockProcessor;
        private readonly Keccak _swapSignatureV3;
        private readonly Keccak _swapSignatureV2;
        private readonly INethermindApi _api;
        private readonly UniswapV3Factory _v3Factory;
        private readonly UniswapV2Factory _v2Factory;
        private Address _usdcAddress = new Address("0xA0b86991c6218b36c1d19D4a2e9Eb0cE3606eB48");
        private Address _v2FactoryAddress = new Address("0x5C69bEe701ef814a2B6a3EDD4B1652CB9cc5aA6f");
        private Address _v3FactoryAddress = new Address("0x1F98431c8aD98523631AE4a59f267346ea31F984");

        public UniswapSource(IBlockProcessor blockProcessor, INethermindApi api)
        {
            _api = api;
            _blockProcessor = blockProcessor;
            
            _swapSignatureV3 = new AbiSignature("Swap",
                AbiType.Address,
                AbiType.Address,
                AbiType.Int256,
                AbiType.Int256,
                AbiType.UInt160,
                AbiType.UInt128,
                AbiType.Int24)
                .Hash;

            _swapSignatureV2 = new AbiSignature("Swap",
                AbiType.Address,
                AbiType.UInt256,
                AbiType.UInt256,
                AbiType.UInt256,
                AbiType.UInt256,
                AbiType.Address)
                .Hash;
            
            _v3Factory = new UniswapV3Factory(_v3FactoryAddress ,_api.CreateBlockchainBridge());
            _v2Factory = new UniswapV2Factory(_v2FactoryAddress, _api.CreateBlockchainBridge());
            
            _blockProcessor.TransactionProcessed += OnTransactionProcessed;
        }

        private void OnTransactionProcessed(object? sender, TxProcessedEventArgs args)
        {
            var logs = args.TxReceipt.Logs;
            
            if(logs is null || !logs.Any()) return;

            var swapLogsV3 = logs.Where(l => l.Topics.Any() && l.Topics.First().Equals(_swapSignatureV3));

            var swapLogsV2 = logs.Where(l => l.Topics.Any() && l.Topics.First().Equals(_swapSignatureV2));

            foreach (var log in swapLogsV3)
            {
                var data = ConvertV3LogToData(log);
                data.Transaction = args.Transaction.Hash;
                data.Token0Price = GetPriceOfTokenInUSDC(data.Token0);
                data.Token1Price = GetPriceOfTokenInUSDC(data.Token1);
                Emit?.Invoke(data);
            }

            foreach (var log in swapLogsV2)
            {
                var data = ConvertV2LogToData(log);
                data.Transaction = args.Transaction.Hash;
                data.Token0Price = GetPriceOfTokenInUSDC(data.Token0);
                data.Token1Price = GetPriceOfTokenInUSDC(data.Token1);
                Emit?.Invoke(data);
            }
        }

        private UniswapData ConvertV3LogToData(LogEntry log)
        {
            var pool = new UniswapV3Pool(log.LoggersAddress, _api.CreateBlockchainBridge());

            var token0Delta = log.Data.Take(32).ToArray().ToInt256();
            var token1Delta = log.Data.Skip(32).Take(32).ToArray().ToInt256();
            
            return new UniswapData
            {
                Swapper = new Address(log.Topics[1]),
                Pool = log.LoggersAddress,
                Token0 = pool.token0(_api.BlockTree.Head.Header),
                Token1 = pool.token1(_api.BlockTree.Head.Header),
                Token0In = (UInt256) (token0Delta > 0 ? token0Delta : 0),
                Token0Out = (UInt256) (token0Delta < 0 ? token0Delta : 0),
                Token1In = (UInt256) (token1Delta > 0 ? token0Delta : 0),
                Token1Out = (UInt256) (token1Delta < 0 ? token1Delta : 0)
            };
        }
        
        private UniswapData ConvertV2LogToData(LogEntry log)
        {
            var pool = new UniswapV2Pool(log.LoggersAddress, _api.CreateBlockchainBridge());
            
            return new UniswapData()
            {
                Swapper = new Address(log.Topics[1]),
                Pool = log.LoggersAddress,
                Token0 = pool.token0(_api.BlockTree.Head.Header),
                Token1 = pool.token1(_api.BlockTree.Head.Header),
                Token0In = log.Data.Take(32).ToArray().ToUInt256(),
                Token0Out = log.Data.Skip(64).Take(32).ToArray().ToUInt256(),
                Token1In = log.Data.Skip(32).Take(32).ToArray().ToUInt256(),
                Token1Out = log.Data.Skip(96).ToArray().ToUInt256()
            };
        }

        private double? GetPriceOfTokenInUSDC(Address token)
        {
            var poolAddress = _v2Factory.getPair(_api.BlockTree.Head.Header, token, _usdcAddress);
            var pool = new UniswapV2Pool(poolAddress, _api.CreateBlockchainBridge());

            var token0 = pool.token0(_api.BlockTree.Head.Header);
            var token1 = pool.token1(_api.BlockTree.Head.Header);
            
            (UInt256, UInt256, uint) reserves = pool.getReserves(_api.BlockTree.Head.Header);
            var token0Reserves = reserves.Item1;
            var token1Reserves = reserves.Item2;

            if (token0 == token) return (double)(token1Reserves / token0Reserves) * Math.Pow(10, 12);
            if (token1 == token) return (double) (token0Reserves / token1Reserves) * Math.Pow(10, 12);

            return null;
        }
    }

    public class UniswapData
    {
        public Keccak? Transaction { get; set; }
        public Address? Swapper { get; set; }
        public Address? Pool { get; set; }
        public Address? Token0 { get; set; }
        public Address? Token1 { get; set; }
        public double? Token0Price { get; set; }
        public double? Token1Price { get; set; }
        public UInt256? Token0In { get; set; }
        public UInt256? Token0Out { get; set; }
        public UInt256? Token1In { get; set; }
        public UInt256? Token1Out { get; set; }
    }
}