// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Abi;
using Nethermind.Blockchain.Contracts;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.AuRa.Contracts
{
    public class RegisterBasedContract(
        IAbiEncoder abiEncoder,
        IRegisterContract registerContract,
        string registryKey,
        AbiDefinition? abiDefinition = null) : Contract(abiEncoder, abiDefinition: abiDefinition)
    {
        private readonly IRegisterContract _registerContract = registerContract;
        private readonly string _registryKey = registryKey;
        private Hash256 _currentHashAddress = Keccak.Zero;

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
