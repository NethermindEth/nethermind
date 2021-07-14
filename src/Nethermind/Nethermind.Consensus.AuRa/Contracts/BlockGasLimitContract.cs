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
using Nethermind.Abi;
using Nethermind.Blockchain.Contracts;
using Nethermind.Blockchain.Contracts.Json;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Consensus.AuRa.Contracts
{
    public interface IBlockGasLimitContract : IActivatedAtBlock
    {
        UInt256? BlockGasLimit(BlockHeader parentHeader);
    }

    public sealed class BlockGasLimitContract : Contract, IBlockGasLimitContract
    {
        private IConstantContract Constant { get; }
        public long Activation { get; }
        
        public BlockGasLimitContract(
            IAbiEncoder abiEncoder, 
            Address contractAddress,
            long transitionBlock,
            IReadOnlyTxProcessorSource readOnlyTxProcessorSource) 
            : base(abiEncoder, contractAddress ?? throw new ArgumentNullException(nameof(contractAddress)))
        {
            Activation = transitionBlock;
            Constant = GetConstant(readOnlyTxProcessorSource);
        }

        public UInt256? BlockGasLimit(BlockHeader parentHeader)
        {
            this.BlockActivationCheck(parentHeader);
            var function = nameof(BlockGasLimit);
            var returnData = Constant.Call(new CallInfo(parentHeader, function, Address.Zero));
            return (returnData?.Length ?? 0) == 0 ? (UInt256?) null : (UInt256) returnData[0];
        }
    }
}
