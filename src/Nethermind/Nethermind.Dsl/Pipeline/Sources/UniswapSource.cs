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
using Nethermind.Abi;
using Nethermind.Blockchain.Processing;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Pipeline;

namespace Nethermind.Dsl.Pipeline.Sources
{
    public class UniswapSource : IPipelineElement<UniswapData>
    {
        public Action<UniswapData> Emit { private get; set; }
        private readonly IBlockProcessor _blockProcessor;
        private readonly AbiSignature _swapSignature;

        public UniswapSource(IBlockProcessor blockProcessor)
        {
            _blockProcessor = blockProcessor;
            _blockProcessor.TransactionProcessed += OnTransactionProcessed;
            _swapSignature = new AbiSignature("Swap", AbiType.Address, AbiType.Address, AbiType.Int256, AbiType.Int256, AbiType.UInt160, AbiType.UInt128,
                AbiType.Int24);
        }

        private void OnTransactionProcessed(object? sender, TxProcessedEventArgs args)
        {
            var logs = args.TxReceipt.Logs;
            
            if(logs is null || !logs.Any()) return;

            var swapLogs = logs.Where(l => l.Topics.First().Equals(_swapSignature.Hash));

            foreach (var log in swapLogs)
            {
                var data = ConvertLogToData(log);
                Emit?.Invoke(data);
            }
        }

        private UniswapData ConvertLogToData(LogEntry log)
        {
            return new()
            {
                Swapper = new Address(log.Topics[1]),
                Pool = log.LoggersAddress,
                TokenADelta = log.Data.Take(32).ToArray().ToInt256(),
                TokenBDelta = log.Data.Skip(32).Take(32).ToArray().ToInt256()
            };
        }
    }

    public class UniswapData
    {
        public Address? Swapper { get; set; }
        public Address? Pool { get; set; }
        public Address? TokenA { get; set; }
        public Address? TokenB { get; set; }
        public decimal TokenAPrice { get; set; }
        public decimal TokenBPrice { get; set; }
        public Int256.Int256 TokenADelta { get; set; }
        public Int256.Int256 TokenBDelta { get; set; }
    }
}