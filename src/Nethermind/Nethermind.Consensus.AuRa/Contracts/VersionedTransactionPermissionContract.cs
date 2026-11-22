// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.Consensus.AuRa.Contracts
{
    public class VersionedTransactionPermissionContract(
        Address contractAddress,
        ulong activation,
        IReadOnlyTxProcessorSource readOnlyTxProcessorSource,
        LruCache<ValueHash256, UInt256> cache,
        ILogManager logManager,
        ISpecProvider specProvider) : VersionedContract<ITransactionPermissionContract>(
            CreateAllVersions(
                    contractAddress,
                    readOnlyTxProcessorSource,
                    specProvider),
            cache,
            activation,
            logManager)
    {
        private static TransactionPermissionContractV1 CreateV1(
            Address contractAddress,
            IReadOnlyTxProcessorSource readOnlyTxProcessorSource) => new(
                contractAddress,
                readOnlyTxProcessorSource);

        private static TransactionPermissionContractV2 CreateV2(
            Address contractAddress,
            IReadOnlyTxProcessorSource readOnlyTxProcessorSource) => new(
                contractAddress,
                readOnlyTxProcessorSource);

        private static TransactionPermissionContractV3 CreateV3(
            Address contractAddress,
            IReadOnlyTxProcessorSource readOnlyTxProcessorSource,
            ISpecProvider specProvider) => new(
                contractAddress,
                readOnlyTxProcessorSource,
                specProvider);

        private static TransactionPermissionContractV4 CreateV4(
            Address contractAddress,
            IReadOnlyTxProcessorSource readOnlyTxProcessorSource,
            ISpecProvider specProvider) => new(
                contractAddress,
                readOnlyTxProcessorSource,
                specProvider);


        private static Dictionary<UInt256, ITransactionPermissionContract> CreateAllVersions(
            Address contractAddress,
            IReadOnlyTxProcessorSource readOnlyTxProcessorSource,
            ISpecProvider specProvider) => new()
            {
                {
                    UInt256.One,
                    CreateV1(
                        contractAddress,
                        readOnlyTxProcessorSource)
                },
                {
                    2,
                    CreateV2(
                        contractAddress,
                        readOnlyTxProcessorSource)
                },
                {
                    3,
                    CreateV3(
                        contractAddress,
                        readOnlyTxProcessorSource,
                        specProvider)
                },
                {
                    4,
                    CreateV4(
                        contractAddress,
                        readOnlyTxProcessorSource,
                        specProvider)
                },
            };
    }
}
