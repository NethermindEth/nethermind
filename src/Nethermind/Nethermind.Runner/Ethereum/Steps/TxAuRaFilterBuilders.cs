//  Copyright (c) 2021 Demerzel Solutions Limited
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

using Nethermind.Blockchain.Processing;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.AuRa.Contracts.DataStore;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Runner.Ethereum.Api;

namespace Nethermind.Runner.Ethereum.Steps
{
    public static class TxAuRaFilterBuilders
    {
        private static ITxFilter CreateBaseAuRaTxFilter(
            IMiningConfig miningConfig,
            AuRaNethermindApi api,
            IReadOnlyTxProcessorSource readOnlyTxProcessorSource,
            IDictionaryContractDataStore<TxPriorityContract.Destination>? minGasPricesContractDataStore)
        {
            ITxFilter gasPriceTxFilter = TxFilterBuilders.CreateStandardTxFilter(miningConfig);

            if (minGasPricesContractDataStore != null)
            {
                gasPriceTxFilter = new MinGasPriceContractTxFilter(gasPriceTxFilter, minGasPricesContractDataStore);
            }
            
            Address? registrar = api.ChainSpec?.Parameters.Registrar;
            if (registrar != null)
            {
                RegisterContract registerContract = new RegisterContract(api.AbiEncoder, registrar, readOnlyTxProcessorSource);
                CertifierContract certifierContract = new CertifierContract(api.AbiEncoder, registerContract, readOnlyTxProcessorSource);
                return new TxCertifierFilter(certifierContract, gasPriceTxFilter, api.LogManager);
            }

            return gasPriceTxFilter;
        }
        
        public static ITxFilter? CreateTxPermissionFilter(AuRaNethermindApi api, IReadOnlyTxProcessorSource readOnlyTxProcessorSource)
        {
            if (api.ChainSpec == null) throw new StepDependencyException(nameof(api.ChainSpec));
            
            if (api.ChainSpec.Parameters.TransactionPermissionContract != null)
            {
                api.TxFilterCache ??= new PermissionBasedTxFilter.Cache();
                
                var txPermissionFilter = new PermissionBasedTxFilter(
                    new VersionedTransactionPermissionContract(api.AbiEncoder,
                        api.ChainSpec.Parameters.TransactionPermissionContract,
                        api.ChainSpec.Parameters.TransactionPermissionContractTransition ?? 0, 
                        readOnlyTxProcessorSource, 
                        api.TransactionPermissionContractVersions,
                        api.LogManager),
                    api.TxFilterCache,
                    api.LogManager);
                
                return txPermissionFilter;
            }

            return null;
        }

        public static ITxFilter CreateAuRaTxFilter(
            IMiningConfig miningConfig,
            AuRaNethermindApi api,
            IReadOnlyTxProcessorSource readOnlyTxProcessorSource,
            IDictionaryContractDataStore<TxPriorityContract.Destination>? minGasPricesContractDataStore)
        {
            ITxFilter baseAuRaTxFilter = CreateBaseAuRaTxFilter(miningConfig, api, readOnlyTxProcessorSource, minGasPricesContractDataStore);
            ITxFilter? txPermissionFilter = CreateTxPermissionFilter(api, readOnlyTxProcessorSource);
            return txPermissionFilter != null
                ? new CompositeTxFilter(baseAuRaTxFilter, txPermissionFilter) 
                : baseAuRaTxFilter;
        }

        public static (TxPriorityContract? Contract, TxPriorityContract.LocalDataSource? DataSource) CreateTxPrioritySources(
            IAuraConfig config, 
            AuRaNethermindApi api,
            IReadOnlyTxProcessorSource readOnlyTxProcessorSource)
        {
            Address.TryParse(config.TxPriorityContractAddress, out Address? txPriorityContractAddress);
            bool usesTxPriorityContract = txPriorityContractAddress != null;

            TxPriorityContract? txPriorityContract = null;
            if (usesTxPriorityContract)
            {
                txPriorityContract = new TxPriorityContract(api.AbiEncoder, txPriorityContractAddress, readOnlyTxProcessorSource); 
            }
            
            string? auraConfigTxPriorityConfigFilePath = config.TxPriorityConfigFilePath;
            bool usesTxPriorityLocalData = auraConfigTxPriorityConfigFilePath != null;
            if (usesTxPriorityLocalData)
            {
                api.TxPriorityContractLocalDataSource ??= new TxPriorityContract.LocalDataSource(auraConfigTxPriorityConfigFilePath, api.EthereumJsonSerializer, api.FileSystem, api.LogManager);
            }

            return (txPriorityContract, api.TxPriorityContractLocalDataSource);
        }

        public static DictionaryContractDataStore<TxPriorityContract.Destination>? CreateMinGasPricesDataStore(
            AuRaNethermindApi api, 
            TxPriorityContract? txPriorityContract, 
            TxPriorityContract.LocalDataSource? localDataSource)
        {
            return txPriorityContract != null || localDataSource != null
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
