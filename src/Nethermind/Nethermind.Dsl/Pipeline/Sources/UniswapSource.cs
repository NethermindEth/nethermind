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
        private uint _usdcDecimals = 6;
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

            var swapLogsV2 = logs.Where(l => l.Topics.Any() && l.Topics.First().Equals(_swapSignatureV2)); foreach (var log in swapLogsV3)
            {
                var data = ConvertV3LogToData(log);
                data.Transaction = args.Transaction.Hash;
                data.Token0Price = $"{GetV2PriceOfTokenInUSDC(data.Token0)} USDC";
                data.Token1Price = $"{GetV2PriceOfTokenInUSDC(data.Token1)} USDC";
                Emit?.Invoke(data);
            }

            foreach (var log in swapLogsV2)
            {
                var data = ConvertV2LogToData(log);
                data.Transaction = args.Transaction.Hash;
                data.Token0Price = $"{GetV2PriceOfTokenInUSDC(data.Token0)} USDC";
                data.Token1Price = $"{GetV2PriceOfTokenInUSDC(data.Token1)} USDC";
                Emit?.Invoke(data);
            }
        }

        private UniswapData ConvertV3LogToData(LogEntry log)
        {
            var pool = new UniswapV3Pool(log.LoggersAddress, _api.CreateBlockchainBridge());

            var token0Delta = log.Data.Take(32).ToArray().ToInt256();
            var token1Delta = log.Data.Skip(32).Take(32).ToArray().ToInt256();
            var token0In = (UInt256) (token0Delta > 0 ? token0Delta : 0);
            var token0Out = (UInt256) (token0Delta < 0 ? token0Delta : 0);
            var token1In = (UInt256) (token1Delta > 0 ? token0Delta : 0);
            var token1Out = (UInt256) (token1Delta < 0 ? token1Delta : 0);
            
            return new UniswapData
            {
                Swapper = new Address(log.Topics[1]),
                Pool = log.LoggersAddress,
                Token0 = pool.token0(_api.BlockTree.Head.Header),
                Token1 = pool.token1(_api.BlockTree.Head.Header),
                Token0In = token0In.ToString(),
                Token0Out = token0Out.ToString(),
                Token1In = token1In.ToString(),
                Token1Out = token1Out.ToString()
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
                Token0In = log.Data.Take(32).ToArray().ToUInt256().ToString(),
                Token0Out = log.Data.Skip(64).Take(32).ToArray().ToUInt256().ToString(),
                Token1In = log.Data.Skip(32).Take(32).ToArray().ToUInt256().ToString(),
                Token1Out = log.Data.Skip(96).ToArray().ToUInt256().ToString()
            };
        }

        //https://ethereum.stackexchange.com/questions/91441/how-can-you-get-the-price-of-token-on-uniswap-using-solidity
        private double? GetV2PriceOfTokenInUSDC(Address tokenAddress)
        {
            var poolAddress = _v2Factory.getPair(_api.BlockTree.Head.Header, tokenAddress, _usdcAddress);
            if (poolAddress == Address.Zero || poolAddress is null) return null; // there might not be any usdc-token pair on v2 for this exact token - fix for later to retrieve prices from v3 as well
            
            var pool = new UniswapV2Pool(poolAddress, _api.CreateBlockchainBridge());

            var token0 = pool.token0(_api.BlockTree.Head.Header);
            var token1 = pool.token1(_api.BlockTree.Head.Header);
            UInt256 tokenReserves = 0;
            UInt256 usdcReserves = 0;

            (UInt256, UInt256, uint) reserves = pool.getReserves(_api.BlockTree.Head.Header);
            var token0Reserves = reserves.Item1;
            var token1Reserves = reserves.Item2;

            ERC20 token = null;

            if (token0 == _usdcAddress)
            {
                token = new ERC20(token1, _api);
                usdcReserves = token0Reserves; 
                tokenReserves = token1Reserves;
            }
            else if (token1 == _usdcAddress)
            {
                token = new ERC20(token0, _api);
                usdcReserves = token1Reserves;
                tokenReserves = token0Reserves;
            }

            if (token is null) return null;

            return ((double)usdcReserves / (double)tokenReserves) * (double)Math.Pow(10, 12);
        }
    }

    public class UniswapData
    {
        public Keccak? Transaction { get; set; }
        public Address? Swapper { get; set; }
        public Address? Pool { get; set; }
        public Address? Token0 { get; set; }
        public Address? Token1 { get; set; }
        public string? Token0Price { get; set; }
        public string? Token1Price { get; set; }
        public string? Token0In { get; set; }
        public string? Token0Out { get; set; }
        public string? Token1In { get; set; }
        public string? Token1Out { get; set; }
    }
}