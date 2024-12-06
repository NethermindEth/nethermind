// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;
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
            LruCache<ValueHash256, UInt256> cache,
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

        private static TransactionPermissionContractV1 CreateV1(
            ISpecProvider specProvider,
            IAbiEncoder abiEncoder,
            Address contractAddress,
            IReadOnlyTxProcessorSource readOnlyTxProcessorSource)
        {
            return new(
                specProvider,
                abiEncoder,
                contractAddress,
                readOnlyTxProcessorSource);
        }

        private static TransactionPermissionContractV2 CreateV2(
            ISpecProvider specProvider,
            IAbiEncoder abiEncoder,
            Address contractAddress,
            IReadOnlyTxProcessorSource readOnlyTxProcessorSource)
        {
            return new(
                specProvider,
                abiEncoder,
                contractAddress,
                readOnlyTxProcessorSource);
        }

        private static TransactionPermissionContractV3 CreateV3(
            ISpecProvider specProvider,
            IAbiEncoder abiEncoder,
            Address contractAddress,
            IReadOnlyTxProcessorSource readOnlyTxProcessorSource)
        {
            return new(
                specProvider,
                abiEncoder,
                contractAddress,
                readOnlyTxProcessorSource);
        }

        private static TransactionPermissionContractV4 CreateV4(
            ISpecProvider specProvider,
            IAbiEncoder abiEncoder,
            Address contractAddress,
            IReadOnlyTxProcessorSource readOnlyTxProcessorSource)
        {
            return new(
                specProvider,
                abiEncoder,
                contractAddress,
                readOnlyTxProcessorSource);
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
                    CreateV1(
                        specProvider,
                        abiEncoder,
                        contractAddress,
                        readOnlyTxProcessorSource)
                },
                {
                    2,
                    CreateV2(
                        specProvider,
                        abiEncoder,
                        contractAddress,
                        readOnlyTxProcessorSource)
                },
                {
                    3,
                    CreateV3(
                        specProvider,
                        abiEncoder,
                        contractAddress,
                        readOnlyTxProcessorSource)
                },
                {
                    4,
                    CreateV4(
                        specProvider,
                        abiEncoder,
                        contractAddress,
                        readOnlyTxProcessorSource)
                },
            };
        }
    }
}
