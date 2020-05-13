//  Copyright (c) 2018 Demerzel Solutions Limited
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
using Nethermind.Abi;
using Nethermind.Blockchain.Processing;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Serialization.Json.Abi;

namespace Nethermind.Consensus.AuRa.Contracts
{
    public class BlockGasLimitContract : Contract, IActivatedAtBlock
    {
        private static readonly AbiDefinition Definition = new AbiDefinitionParser().Parse<BlockGasLimitContract>();
        private ConstantContract Constant { get; }
        public long ActivationBlock { get; }
        
        public BlockGasLimitContract(
            ITransactionProcessor transactionProcessor, 
            IAbiEncoder abiEncoder, 
            Address contractAddress,
            long transitionBlock,
            IReadOnlyTransactionProcessorSource readOnlyTransactionProcessorSource) 
            : base(transactionProcessor, abiEncoder, contractAddress)
        {
            ActivationBlock = transitionBlock;
            Constant = GetConstant(readOnlyTransactionProcessorSource);
        }

        public UInt256? BlockGasLimit(BlockHeader parentHeader)
        {
            this.ActivationCheck(parentHeader);
            var function = Definition.GetFunction(nameof(BlockGasLimit));
            var bytes = Constant.CallRaw(parentHeader, function, Address.Zero);
            return (bytes?.Length ?? 0) == 0 ? (UInt256?) null : (UInt256) AbiEncoder.Decode(function.GetReturnInfo(), bytes)[0];
        }
    }
}