// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
        public AuRaNethermindApi(Dependencies dependencies)
            : base(dependencies)
        {
        }

        public new IAuRaBlockFinalizationManager? FinalizationManager
        {
            get => base.FinalizationManager as IAuRaBlockFinalizationManager;
            set => base.FinalizationManager = value;
        }

        private TxAuRaFilterBuilders? _txAuRaFilterBuilders = null;

        public TxAuRaFilterBuilders TxAuRaFilterBuilders => _txAuRaFilterBuilders ??= new TxAuRaFilterBuilders(
            this.ChainSpec,
            this.SpecProvider,
            this.Config<IBlocksConfig>(),
            this.Config<IAuraConfig>(),
            this.AbiEncoder,
            this.WorldStateManager!,
            this.BlockTree!,
            this.ReceiptFinder!,
            this.AuraStatefulComponents,
            this.TxFilterCache,
            this.LogManager
        );

        private PermissionBasedTxFilter.Cache? _txFilterCache = null;
        private PermissionBasedTxFilter.Cache TxFilterCache => _txFilterCache ??= new PermissionBasedTxFilter.Cache();

        private IValidatorStore? _validatorStore = null;
        public IValidatorStore ValidatorStore => _validatorStore ??= new ValidatorStore(DbProvider!.BlockInfosDb);

        private AuraStatefulComponents? _auraStatefulComponents = null;
        private AuraStatefulComponents AuraStatefulComponents => _auraStatefulComponents ??= new AuraStatefulComponents();

        private AuRaContractGasLimitOverride.Cache? _gasLimitCalculatorCache = null;
        public AuRaContractGasLimitOverride.Cache GasLimitCalculatorCache => _gasLimitCalculatorCache ??= new AuRaContractGasLimitOverride.Cache();

        public IReportingValidator? ReportingValidator { get; set; }

        public ReportingContractBasedValidator.Cache ReportingContractValidatorCache { get; } = new ReportingContractBasedValidator.Cache();


        private TxPriorityContract.LocalDataSource? _txPriorityContractLocalDataSource = null;
        public TxPriorityContract.LocalDataSource? TxPriorityContractLocalDataSource
        {
            get
            {
                if (_txPriorityContractLocalDataSource is not null) return _txPriorityContractLocalDataSource;

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

        public StartBlockProducerAuRa CreateStartBlockProducer()
        {
            return new StartBlockProducerAuRa(
                this.SpecProvider,
                this.ChainSpec,
                this.ConfigProvider.GetConfig<IBlocksConfig>(),
                this.Config<IAuraConfig>(),
                this.BlockProcessingQueue!,
                this.BlockTree!,
                this.Sealer!,
                this.Timestamper,
                this.ReportingValidator!,
                this.ReceiptStorage!,
                this.ValidatorStore,
                this.FinalizationManager!,
                this.EngineSigner!,
                this.GasPriceOracle!,
                this.ReportingContractValidatorCache,
                this.DisposeStack,
                this.GasLimitCalculatorCache!,
                this.AbiEncoder,
                this.WorldStateManager!,
                this.TxAuRaFilterBuilders!,
                this.TxPriorityContractLocalDataSource!,
                this.TxPool!,
                this.StateReader,
                this.TransactionComparerProvider,
                this.BlockPreprocessor,
                this.NodeKey,
                this.CryptoRandom,
                this.BlockValidator,
                this.RewardCalculatorSource,
                this.BlockProducerEnvFactory,
                this.LogManager);
        }
    }

    public class AuraStatefulComponents
    {
        public LruCache<ValueHash256, UInt256> TransactionPermissionContractVersions { get; }
            = new(
                PermissionBasedTxFilter.Cache.MaxCacheSize,
                nameof(TransactionPermissionContract));
    }
}
