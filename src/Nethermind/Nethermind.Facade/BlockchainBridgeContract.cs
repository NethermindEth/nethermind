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
using System.Threading;
using Nethermind.Abi;
using Nethermind.Blockchain.Contracts;
using Nethermind.Core;

namespace Nethermind.Facade
{
    public abstract class BlockchainBridgeContract : Contract
    {
        public BlockchainBridgeContract(IAbiEncoder abiEncoder, Address contractAddress, AbiDefinition? abiDefinition = null) : base(abiEncoder, contractAddress, abiDefinition)
        {
        }
        
        /// <summary>
        /// Gets constant version of the contract. Allowing to call contract methods without state modification.
        /// </summary>
        /// <param name="blockchainBridge"><see cref="IBlockchainBridge"/> to call transactions.</param>
        /// <returns>Constant version of the contract.</returns>
        protected IConstantContract GetConstant(IBlockchainBridge blockchainBridge) =>
            new ConstantBridgeContract(this, blockchainBridge);

        private class ConstantBridgeContract : ConstantContractBase
        {
            private readonly IBlockchainBridge _blockchainBridge;

            public ConstantBridgeContract(Contract contract, IBlockchainBridge blockchainBridge)
                : base(contract)
            {
                _blockchainBridge = blockchainBridge ?? throw new ArgumentNullException(nameof(blockchainBridge));
            }

            public override object[] Call(CallInfo callInfo)
            {
                var transaction = GenerateTransaction(callInfo);
                var result = _blockchainBridge.Call(callInfo.ParentHeader, transaction, CancellationToken.None);
                if (!string.IsNullOrEmpty(result.Error))
                {
                    throw new AbiException(result.Error);
                }
                
                return callInfo.Result = DecodeReturnData(callInfo.FunctionName, result.OutputData);
            }
        }
    }
}
