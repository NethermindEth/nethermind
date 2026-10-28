// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Blockchain.Contracts;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.AuRa.Contracts
{
    public class RegisterBasedContract(
        IRegisterContract registerContract,
        string registryKey) : Contract
    {
        private readonly IRegisterContract _registerContract = registerContract;
        private readonly string _registryKey = registryKey;
        private readonly Lock _lock = new();
        private Hash256? _resolvedAtHash;

        protected override Address? GetContractAddress(BlockHeader? header)
        {
            if (header is null)
            {
                return ContractAddress;
            }

            lock (_lock)
            {
                if (_resolvedAtHash != header.Hash)
                {
                    if (_registerContract.TryGetAddress(header, _registryKey, out Address contractAddress))
                    {
                        ContractAddress = contractAddress;
                        _resolvedAtHash = header.Hash;
                    }
                    else
                    {
                        ContractAddress = null;
                        _resolvedAtHash = null;
                    }
                }

                return ContractAddress;
            }
        }
    }
}
