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
            AbiDefinition abiDefinition = null) 
            : base(abiEncoder, Address.Zero, abiDefinition)
        {
            _registerContract = registerContract;
            _registryKey = registryKey;
        }

        protected override Transaction GenerateTransaction<T>(byte[] transactionData, Address sender, long gasLimit = DefaultContractGasLimit, BlockHeader header = null)
        {
            Address contractAddress = GetContractAddress(header);
            return GenerateTransaction<T>(transactionData, sender, contractAddress, gasLimit);
        }

        private Address GetContractAddress(BlockHeader header)
        {
            bool needUpdate = false;
            lock (_currentHashAddress)
            {
                needUpdate = header != null && _currentHashAddress != header.Hash; 
            }
            
            if (needUpdate)
            {
                Address contractAddress = _registerContract.GetAddress(header, _registryKey);
                if (contractAddress == Address.Zero)
                {
                    throw new AbiException($"Contract {GetType().Name} is not configured in Register Contract under key {_registryKey}.");
                }

                lock (_currentHashAddress)
                {
                    ContractAddress = contractAddress;
                    _currentHashAddress = header.Hash;
                }
                
                return contractAddress;
            }

            return ContractAddress;

        }
    }
}
