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
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.AuRa.Contracts
{
    public class RegisterBasedContract : Contract
    {
        private readonly IRegisterContract _registerContract;
        private readonly string _registryKey;
        private Keccak _currentHashAddress = Keccak.Zero;

        public RegisterBasedContract(
            IAbiEncoder abiEncoder,
            IRegisterContract registerContract,
            string registryKey,
            AbiDefinition? abiDefinition = null) 
            : base(abiEncoder, abiDefinition:abiDefinition)
        {
            _registerContract = registerContract;
            _registryKey = registryKey;
        }

        protected override Transaction GenerateTransaction<T>(Address? contractAddress, byte[] transactionData, Address sender, long gasLimit = DefaultContractGasLimit, BlockHeader header = null)
        {
            return GenerateTransaction<T>(GetContractAddress(header), transactionData, sender, gasLimit);
        }

        private Address GetContractAddress(BlockHeader? header)
        {
            bool needUpdate = false;
            lock (_currentHashAddress)
            {
                needUpdate = header != null && _currentHashAddress != header.Hash; 
            }
            
            if (needUpdate)
            {
                if (_registerContract.TryGetAddress(header, _registryKey, out Address contractAddress))
                {
                    lock (_currentHashAddress)
                    {
                        ContractAddress = contractAddress;
                        _currentHashAddress = header.Hash!;
                    }
                }

                return contractAddress;
            }

            return ContractAddress;

        }
    }
}
