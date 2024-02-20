// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.AuRa.Contracts.DataStore;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Init.Steps;

namespace Nethermind.Consensus.AuRa.InitializationSteps
{
    public static class TxAuRaFilterBuilders
    {
        /// <summary>
        /// Filter decorator.
        /// <remarks>
        /// Allow to create new filter based on original filter and a potential fallbackFilter if original filter was not used.
        /// </remarks>
        /// </summary>
        public delegate ITxFilter FilterDecorator(ITxFilter originalFilter, ITxFilter? fallbackFilter = null);

        /// <summary>
        /// Delegate factory method to create final filter for AuRa.
        /// </summary>
        /// <remarks>
        /// This is used to decorate original filter with <see cref="AuRaMergeTxFilter"/> in order to disable it post-merge.
        /// </remarks>
        public static FilterDecorator CreateFilter { get; set; } = (x, _) => x;

        private static ITxFilter CreateBaseAuRaTxFilter(
            AuRaNethermindApi api,
            IDictionaryContractDataStore<TxPriorityContract.Destination>? minGasPricesContractDataStore)
        {
            ISpecProvider specProvider = api.SpecProvider!;
            IBlocksConfig blocksConfig = api.Config<IBlocksConfig>();
            IMinGasPriceTxFilter minGasPriceTxFilter = TxFilterBuilders.CreateStandardMinGasPriceTxFilter(blocksConfig, specProvider);
            ITxFilter gasPriceTxFilter = minGasPriceTxFilter;
            if (minGasPricesContractDataStore is not null)
            {
                gasPriceTxFilter = CreateFilter(new MinGasPriceContractTxFilter(minGasPriceTxFilter, minGasPricesContractDataStore), minGasPriceTxFilter);
            }

            Address? registrar = api.ChainSpec?.Parameters.Registrar;
            if (registrar is not null)
            {
                RegisterContract registerContract = new(api.AbiEncoder, registrar, api.CreateReadOnlyTransactionProcessorSource());
                CertifierContract certifierContract = new(api.AbiEncoder, registerContract, api.CreateReadOnlyTransactionProcessorSource());
                return CreateFilter(new TxCertifierFilter(certifierContract, gasPriceTxFilter, specProvider, api.LogManager), gasPriceTxFilter);
            }

            return gasPriceTxFilter;
        }

        private static ITxFilter CreateBaseAuRaTxFilter(
            AuRaNethermindApi api,
            ITxFilter baseTxFilter)
        {
            Address? registrar = api.ChainSpec?.Parameters.Registrar;
            if (registrar is not null)
            {
                RegisterContract registerContract = new(api.AbiEncoder, registrar, api.CreateReadOnlyTransactionProcessorSource());
                CertifierContract certifierContract = new(api.AbiEncoder, registerContract, api.CreateReadOnlyTransactionProcessorSource());
                return CreateFilter(new TxCertifierFilter(certifierContract, baseTxFilter, api.SpecProvider, api.LogManager));
            }

            return baseTxFilter;
        }


        public static ITxFilter? CreateTxPermissionFilter(AuRaNethermindApi api)
        {
            if (api.ChainSpec is null) throw new StepDependencyException(nameof(api.ChainSpec));
            if (api.SpecProvider is null) throw new StepDependencyException(nameof(api.SpecProvider));

            if (api.ChainSpec.Parameters.TransactionPermissionContract is not null)
            {
                var txPermissionFilter = CreateFilter(new PermissionBasedTxFilter(
                    new VersionedTransactionPermissionContract(api.AbiEncoder,
                        api.ChainSpec.Parameters.TransactionPermissionContract,
                        api.ChainSpec.Parameters.TransactionPermissionContractTransition ?? 0,
                        api.CreateReadOnlyTransactionProcessorSource(),
                        api.TransactionPermissionContractVersions,
                        api.LogManager,
                        api.SpecProvider),
                    api.TxFilterCache,
                    api.LogManager));

                return txPermissionFilter;
            }

            return null;
        }

        public static ITxFilter CreateAuRaTxFilterForProducer(
            AuRaNethermindApi api,
            IDictionaryContractDataStore<TxPriorityContract.Destination>? minGasPricesContractDataStore)
        {
            ITxFilter baseAuRaTxFilter = CreateBaseAuRaTxFilter(api, minGasPricesContractDataStore);
            ITxFilter? txPermissionFilter = CreateTxPermissionFilter(api);
            return txPermissionFilter is not null
                ? new CompositeTxFilter(baseAuRaTxFilter, txPermissionFilter)
                : baseAuRaTxFilter;
        }

        public static ITxFilter CreateAuRaTxFilter(AuRaNethermindApi api, ITxFilter baseTxFilter)
        {
            ITxFilter baseAuRaTxFilter = CreateBaseAuRaTxFilter(api, baseTxFilter);
            ITxFilter? txPermissionFilter = CreateTxPermissionFilter(api);
            return txPermissionFilter is not null
                ? new CompositeTxFilter(baseAuRaTxFilter, txPermissionFilter)
                : baseAuRaTxFilter;
        }

        public static TxPriorityContract? CreateTxPrioritySources(AuRaNethermindApi api)
        {
            IAuraConfig config = api.Config<IAuraConfig>();
            Address.TryParse(config.TxPriorityContractAddress, out Address? txPriorityContractAddress);
            bool usesTxPriorityContract = txPriorityContractAddress is not null;

            TxPriorityContract? txPriorityContract = null;
            if (usesTxPriorityContract)
            {
                txPriorityContract = new TxPriorityContract(api.AbiEncoder, txPriorityContractAddress, api.CreateReadOnlyTransactionProcessorSource());
            }

            return txPriorityContract;
        }

        public static DictionaryContractDataStore<TxPriorityContract.Destination>? CreateMinGasPricesDataStore(
            AuRaNethermindApi api,
            TxPriorityContract? txPriorityContract,
            TxPriorityContract.LocalDataSource? localDataSource)
        {
            return txPriorityContract is not null || localDataSource is not null
                ? new DictionaryContractDataStore<TxPriorityContract.Destination>(
                    new TxPriorityContract.DestinationSortedListContractDataStoreCollection(),
                    txPriorityContract?.MinGasPrices,
                    api.BlockTree,
                    api.ReceiptFinder,
                    api.LogManager,
                    localDataSource?.GetMinGasPricesLocalDataSource())
                : null;
        }
    }
}
