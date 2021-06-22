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
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Processing;
using Nethermind.Core;
using Nethermind.Facade;
using Nethermind.Pipeline;

namespace Nethermind.Dsl.Pipeline.Sources
{
    public class UniswapSource<TOut> : IPipelineElement<TOut> where TOut : Transaction
    {
        public Action<TOut> Emit { get; set; }
        private readonly IBlockProcessor _blockProcessor;
        private readonly IBlockchainBridge _blockchainBridge;
        private readonly IBlockFinder _blockFinder;
        private readonly UniswapContract _contract;
        private readonly Address _pairAddress;

        public UniswapSource(IBlockProcessor blockProcessor, IBlockchainBridge blockchainBridge, IBlockFinder blockFinder, string tokenA, string tokenB)
        {
            _blockProcessor = blockProcessor;
            _blockchainBridge = blockchainBridge;
            _blockFinder = blockFinder;
            _contract = new UniswapContract(new Address("0x5C69bEe701ef814a2B6a3EDD4B1652CB9cc5aA6f"), blockchainBridge);
            var header = blockFinder.FindHeader(10000835);
            if (header is null)
            {
                throw new Exception("Could not find block header at 10000835 block.");
            }
            _pairAddress = _contract.getPair(header,new Address(tokenA), new Address(tokenB));

            _blockProcessor.TransactionProcessed += OnTransactionProcessed;
        }

        private void OnTransactionProcessed(object? sender, TxProcessedEventArgs args)
        {
            if (args.Transaction.To == _pairAddress)
            {
                Emit?.Invoke((TOut) args.Transaction);
            }
        }
    }

    public class UniswapContract : BlockchainBridgeContract
    {
        private IConstantContract ConstantContract { get; set; }
        public readonly Address ContractAddress; 
        
        public UniswapContract(Address contractAddress, IBlockchainBridge blockchainBridge) : base(contractAddress)
        {
            ContractAddress = contractAddress;
            ConstantContract = GetConstant(blockchainBridge);
        }

        public Address getPair(BlockHeader header, Address tokenA, Address tokenB)
        {
            return ConstantContract.Call<Address>(header, nameof(getPair), Address.Zero, tokenA, tokenB);
        }
    }
}