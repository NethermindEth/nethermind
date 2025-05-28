// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Abi;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.AuRa.Contracts.DataStore;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Consensus.AuRa.InitializationSteps
{
    public class TxAuRaFilterBuilders(
        ChainSpec chainSpec,
        ISpecProvider specProvider,
        IBlocksConfig blocksConfig,
        IAuraConfig auraConfig,
        IAbiEncoder abiEncoder,
        IReadOnlyTxProcessingEnvFactory readOnlyTxProcessingEnvFactory,
        IBlockTree blockTree,
        IReceiptFinder receiptFinder,
        AuraStatefulComponents statefulComponents,
        PermissionBasedTxFilter.Cache txFilterCache,
        ILogManager logManager
    )
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
        public static FilterDecorator CreateFilter { get; set; } = static (x, _) => x;

        private ITxFilter CreateBaseAuRaTxFilter(
            IDictionaryContractDataStore<TxPriorityContract.Destination>? minGasPricesContractDataStore)
        {
            IMinGasPriceTxFilter minGasPriceTxFilter = TxFilterBuilders.CreateStandardMinGasPriceTxFilter(blocksConfig, specProvider);
            ITxFilter gasPriceTxFilter = minGasPriceTxFilter;
            if (minGasPricesContractDataStore is not null)
            {
                gasPriceTxFilter = CreateFilter(new MinGasPriceContractTxFilter(minGasPriceTxFilter, minGasPricesContractDataStore), minGasPriceTxFilter);
            }

            Address? registrar = chainSpec?.Parameters.Registrar;
            if (registrar is not null)
            {
                RegisterContract registerContract = new(abiEncoder, registrar, readOnlyTxProcessingEnvFactory.Create());
                CertifierContract certifierContract = new(abiEncoder, registerContract, readOnlyTxProcessingEnvFactory.Create());
                return CreateFilter(new TxCertifierFilter(certifierContract, gasPriceTxFilter, specProvider, logManager), gasPriceTxFilter);
            }

            return gasPriceTxFilter;
        }

        private ITxFilter CreateBaseAuRaTxFilter(ITxFilter baseTxFilter)
        {
            Address? registrar = chainSpec?.Parameters.Registrar;
            if (registrar is not null)
            {
                RegisterContract registerContract = new(abiEncoder, registrar, readOnlyTxProcessingEnvFactory.Create());
                CertifierContract certifierContract = new(abiEncoder, registerContract, readOnlyTxProcessingEnvFactory.Create());
                return CreateFilter(new TxCertifierFilter(certifierContract, baseTxFilter, specProvider, logManager));
            }

            return baseTxFilter;
        }


        public ITxFilter? CreateTxPermissionFilter()
        {
            if (chainSpec.Parameters.TransactionPermissionContract is not null)
            {
                var txPermissionFilter = CreateFilter(new PermissionBasedTxFilter(
                    new VersionedTransactionPermissionContract(abiEncoder,
                        chainSpec.Parameters.TransactionPermissionContract,
                        chainSpec.Parameters.TransactionPermissionContractTransition ?? 0,
                        readOnlyTxProcessingEnvFactory.Create(),
                        statefulComponents.TransactionPermissionContractVersions,
                        logManager,
                        specProvider),
                    txFilterCache,
                    logManager));

                return txPermissionFilter;
            }

            return null;
        }

        public ITxFilter CreateAuRaTxFilterForProducer(IDictionaryContractDataStore<TxPriorityContract.Destination>? minGasPricesContractDataStore)
        {
            ITxFilter baseAuRaTxFilter = CreateBaseAuRaTxFilter(minGasPricesContractDataStore);
            ITxFilter? txPermissionFilter = CreateTxPermissionFilter();
            return txPermissionFilter is not null
                ? new CompositeTxFilter(baseAuRaTxFilter, txPermissionFilter)
                : baseAuRaTxFilter;
        }

        public ITxFilter CreateAuRaTxFilter(ITxFilter baseTxFilter)
        {
            ITxFilter baseAuRaTxFilter = CreateBaseAuRaTxFilter(baseTxFilter);
            ITxFilter? txPermissionFilter = CreateTxPermissionFilter();
            return txPermissionFilter is not null
                ? new CompositeTxFilter(baseAuRaTxFilter, txPermissionFilter)
                : baseAuRaTxFilter;
        }

        public TxPriorityContract? CreateTxPrioritySources()
        {
            Address.TryParse(auraConfig.TxPriorityContractAddress, out Address? txPriorityContractAddress);
            bool usesTxPriorityContract = txPriorityContractAddress is not null;

            TxPriorityContract? txPriorityContract = null;
            if (usesTxPriorityContract)
            {
                txPriorityContract = new TxPriorityContract(abiEncoder, txPriorityContractAddress, readOnlyTxProcessingEnvFactory.Create());
            }

            return txPriorityContract;
        }

        public DictionaryContractDataStore<TxPriorityContract.Destination>? CreateMinGasPricesDataStore(
            TxPriorityContract? txPriorityContract,
            TxPriorityContract.LocalDataSource? localDataSource)
        {
            return txPriorityContract is not null || localDataSource is not null
                ? new DictionaryContractDataStore<TxPriorityContract.Destination>(
                    new TxPriorityContract.DestinationSortedListContractDataStoreCollection(),
                    txPriorityContract?.MinGasPrices,
                    blockTree,
                    receiptFinder,
                    logManager,
                    localDataSource?.GetMinGasPricesLocalDataSource())
                : null;
        }
    }
}
