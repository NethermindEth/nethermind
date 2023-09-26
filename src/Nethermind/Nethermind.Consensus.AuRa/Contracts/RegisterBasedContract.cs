// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
            : base(abiEncoder, abiDefinition: abiDefinition)
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
                needUpdate = header is not null && _currentHashAddress != header.Hash;
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
