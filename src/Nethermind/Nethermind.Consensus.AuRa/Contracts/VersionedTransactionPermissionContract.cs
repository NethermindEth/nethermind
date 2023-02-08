// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;

namespace Nethermind.Consensus.AuRa.Contracts
{
    public class VersionedTransactionPermissionContract : VersionedContract<ITransactionPermissionContract>
    {
        public VersionedTransactionPermissionContract(IAbiEncoder abiEncoder,
            Address contractAddress,
            long activation,
            IReadOnlyTxProcessorSource readOnlyTxProcessorSource,
            LruCache<KeccakKey, UInt256> cache,
            ILogManager logManager,
            ISpecProvider specProvider)
            : base(
                CreateAllVersions(abiEncoder,
                    contractAddress,
                    readOnlyTxProcessorSource,
                    specProvider),
                cache,
                activation,
                logManager)
        {
        }

        private static TransactionPermissionContractV1 CreateV1(IAbiEncoder abiEncoder,
            Address contractAddress,
            IReadOnlyTxProcessorSource readOnlyTxProcessorSource)
        {
            return new(
                abiEncoder,
                contractAddress,
                readOnlyTxProcessorSource);
        }

        private static TransactionPermissionContractV2 CreateV2(IAbiEncoder abiEncoder,
            Address contractAddress,
            IReadOnlyTxProcessorSource readOnlyTxProcessorSource)
        {
            return new(
                abiEncoder,
                contractAddress,
                readOnlyTxProcessorSource);
        }

        private static TransactionPermissionContractV3 CreateV3(IAbiEncoder abiEncoder,
            Address contractAddress,
            IReadOnlyTxProcessorSource readOnlyTxProcessorSource,
            ISpecProvider specProvider)
        {
            return new(
                abiEncoder,
                contractAddress,
                readOnlyTxProcessorSource,
                specProvider);
        }

        private static TransactionPermissionContractV4 CreateV4(IAbiEncoder abiEncoder,
            Address contractAddress,
            IReadOnlyTxProcessorSource readOnlyTxProcessorSource,
            ISpecProvider specProvider)
        {
            return new(
                abiEncoder,
                contractAddress,
                readOnlyTxProcessorSource,
                specProvider);
        }


        private static Dictionary<UInt256, ITransactionPermissionContract> CreateAllVersions(IAbiEncoder abiEncoder,
            Address contractAddress,
            IReadOnlyTxProcessorSource readOnlyTxProcessorSource,
            ISpecProvider specProvider)
        {
            return new()
            {
                {
                    UInt256.One,
                    CreateV1(abiEncoder,
                        contractAddress,
                        readOnlyTxProcessorSource)
                },
                {
                    2,
                    CreateV2(abiEncoder,
                        contractAddress,
                        readOnlyTxProcessorSource)
                },
                {
                    3,
                    CreateV3(abiEncoder,
                        contractAddress,
                        readOnlyTxProcessorSource,
                        specProvider)
                },
                {
                    4,
                    CreateV4(abiEncoder,
                        contractAddress,
                        readOnlyTxProcessorSource,
                        specProvider)
                },
            };
        }
    }
}
