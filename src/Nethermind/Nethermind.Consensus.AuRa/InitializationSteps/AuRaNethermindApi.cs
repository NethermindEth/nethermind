// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using Autofac;
using Nethermind.Api;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Json;

namespace Nethermind.Consensus.AuRa.InitializationSteps
{
    public class AuRaNethermindApi : NethermindApi
    {
        public AuRaNethermindApi(Dependencies dependencies)
            : base(dependencies)
        {
        }

        public new IAuRaBlockFinalizationManager? FinalizationManager
        {
            get => base.FinalizationManager as IAuRaBlockFinalizationManager;
            set => base.FinalizationManager = value;
        }

        public TxAuRaFilterBuilders TxAuRaFilterBuilders => Context.Resolve<TxAuRaFilterBuilders>();
        public IValidatorStore ValidatorStore => Context.Resolve<IValidatorStore>();
        public AuRaContractGasLimitOverride.Cache GasLimitCalculatorCache => Context.Resolve<AuRaContractGasLimitOverride.Cache>();
        public AuraStatefulComponents AuraStatefulComponents => Context.Resolve<AuraStatefulComponents>();
        public IReportingValidator ReportingValidator => Context.Resolve<IReportingValidator>();
        public ReportingContractBasedValidator.Cache ReportingContractValidatorCache => Context.Resolve<ReportingContractBasedValidator.Cache>();
        public StartBlockProducerAuRa CreateStartBlockProducer() => Context.Resolve<StartBlockProducerAuRa>();
    }

    public class AuraStatefulComponents(IAuraConfig auraConfig, IJsonSerializer jsonSerializer, IFileSystem fileSystem, ILogManager logManager)
    {
        public LruCache<ValueHash256, UInt256> TransactionPermissionContractVersions { get; }
            = new(
                PermissionBasedTxFilter.Cache.MaxCacheSize,
                nameof(TransactionPermissionContract));


        private TxPriorityContract.LocalDataSource? _txPriorityContractLocalDataSource = null;
        public TxPriorityContract.LocalDataSource? TxPriorityContractLocalDataSource
        {
            get
            {
                if (_txPriorityContractLocalDataSource is not null) return _txPriorityContractLocalDataSource;

                IAuraConfig config = auraConfig;
                string? auraConfigTxPriorityConfigFilePath = config.TxPriorityConfigFilePath;
                bool usesTxPriorityLocalData = auraConfigTxPriorityConfigFilePath is not null;
                if (usesTxPriorityLocalData)
                {
                    _txPriorityContractLocalDataSource = new TxPriorityContract.LocalDataSource(
                        auraConfigTxPriorityConfigFilePath,
                        jsonSerializer,
                        fileSystem,
                        logManager);
                }

                return _txPriorityContractLocalDataSource;
            }
        }
    }
}
