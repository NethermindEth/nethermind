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

using System.Collections.Generic;
using System.Linq;
using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;

namespace Nethermind.Consensus.AuRa.Contracts
{
    public class VersionedTransactionPermissionContract : VersionedContract<ITransactionPermissionContract>
    {
        public VersionedTransactionPermissionContract(IAbiEncoder abiEncoder,
            Address contractAddress,
            long activation,
            IReadOnlyTransactionProcessorSource readOnlyTransactionProcessorSource, 
            ICache<Keccak, UInt256> cache)
            : base(
                CreateAllVersions(abiEncoder,
                    contractAddress,
                    readOnlyTransactionProcessorSource),
                cache,
                activation)
        {
        }
        
        private static TransactionPermissionContractV1 CreateV1(IAbiEncoder abiEncoder,
            Address contractAddress,
            IReadOnlyTransactionProcessorSource readOnlyTransactionProcessorSource)
        {
            return new TransactionPermissionContractV1(
                abiEncoder,
                contractAddress,
                readOnlyTransactionProcessorSource);
        }

        private static TransactionPermissionContractV2 CreateV2(IAbiEncoder abiEncoder,
            Address contractAddress,
            IReadOnlyTransactionProcessorSource readOnlyTransactionProcessorSource)
        {
            return new TransactionPermissionContractV2(
                abiEncoder,
                contractAddress,
                readOnlyTransactionProcessorSource);
        }

        private static TransactionPermissionContractV3 CreateV3(IAbiEncoder abiEncoder,
            Address contractAddress,
            IReadOnlyTransactionProcessorSource readOnlyTransactionProcessorSource)
        {
            return new TransactionPermissionContractV3(
                abiEncoder,
                contractAddress,
                readOnlyTransactionProcessorSource);
        }

        private static Dictionary<UInt256, ITransactionPermissionContract> CreateAllVersions(IAbiEncoder abiEncoder,
            Address contractAddress,
            IReadOnlyTransactionProcessorSource readOnlyTransactionProcessorSource)
        {
            return new Dictionary<UInt256, ITransactionPermissionContract>
            {
                {
                    UInt256.One, CreateV1(abiEncoder,
                        contractAddress,
                        readOnlyTransactionProcessorSource)
                },
                {
                    2, CreateV2(abiEncoder,
                        contractAddress,
                        readOnlyTransactionProcessorSource)
                },
                {
                    3, CreateV3(abiEncoder,
                        contractAddress,
                        readOnlyTransactionProcessorSource)
                },
            };
        }
    }
}
