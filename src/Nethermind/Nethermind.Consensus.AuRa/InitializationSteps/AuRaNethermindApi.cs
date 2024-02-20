// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Consensus.Processing;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Consensus.AuRa.InitializationSteps
{
    public class AuRaNethermindApi : NethermindApi
    {
        public AuRaNethermindApi(ILifetimeScope container)
            : base(container)
        {
        }

        public new IAuRaBlockFinalizationManager? FinalizationManager
        {
            get => base.FinalizationManager as IAuRaBlockFinalizationManager;
            set => base.FinalizationManager = value;
        }

        private PermissionBasedTxFilter.Cache? _txFilterCache = null;
        public PermissionBasedTxFilter.Cache TxFilterCache => _txFilterCache ??= new PermissionBasedTxFilter.Cache();

        private IValidatorStore? _validatorStore = null;
        public IValidatorStore ValidatorStore => _validatorStore ??= new ValidatorStore(DbProvider!.BlockInfosDb);

        public LruCache<ValueHash256, UInt256> TransactionPermissionContractVersions { get; }
            = new(
                PermissionBasedTxFilter.Cache.MaxCacheSize,
                nameof(TransactionPermissionContract));


        private AuRaContractGasLimitOverride.Cache? _gasLimitCalculatorCache = null;
        public AuRaContractGasLimitOverride.Cache GasLimitCalculatorCache => _gasLimitCalculatorCache ??= new AuRaContractGasLimitOverride.Cache();

        public IReportingValidator? ReportingValidator { get; set; }

        public ReportingContractBasedValidator.Cache ReportingContractValidatorCache { get; } = new ReportingContractBasedValidator.Cache();
        public IGasLimitCalculator? AuraGasLimitCalculator { get; set; }

        private TxPriorityContract.LocalDataSource? _txPriorityContractLocalDataSource = null;
        public TxPriorityContract.LocalDataSource? TxPriorityContractLocalDataSource
        {
            get
            {
                if (_txPriorityContractLocalDataSource != null) return _txPriorityContractLocalDataSource;

                IAuraConfig config = this.Config<IAuraConfig>();
                string? auraConfigTxPriorityConfigFilePath = config.TxPriorityConfigFilePath;
                bool usesTxPriorityLocalData = auraConfigTxPriorityConfigFilePath is not null;
                if (usesTxPriorityLocalData)
                {
                    _txPriorityContractLocalDataSource = new TxPriorityContract.LocalDataSource(
                        auraConfigTxPriorityConfigFilePath,
                        EthereumJsonSerializer,
                        FileSystem,
                        LogManager);
                }

                return _txPriorityContractLocalDataSource;
            }
        }

        public ReadOnlyTxProcessingEnv CreateReadOnlyTransactionProcessorSource() =>
            new ReadOnlyTxProcessingEnv(WorldStateManager!, BlockTree!.AsReadOnly(), SpecProvider!, LogManager!);
    }
}
