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

using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Abi;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.Validators;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Repositories;
using Nethermind.Db.Blooms;
using Nethermind.Runner.Ethereum.Api;
using Nethermind.Synchronization.BeamSync;
using Nethermind.TxPool;
using Nethermind.TxPool.Storages;
using Nethermind.Vault;
using Nethermind.Vault.Config;

namespace Nethermind.Runner.Ethereum.Steps
{
    [RunnerStepDependencies(typeof(InitRlp), typeof(InitDatabase), typeof(SetupKeyStore))]
    public class InitializeBlockchain : IStep
    {
        private readonly NethermindApi _api;

        public InitializeBlockchain(NethermindApi api)
        {
            _api = api;
        }

        public async Task Execute(CancellationToken _)
        {
            await InitBlockchain();
        }

        [Todo(Improve.Refactor, "Use chain spec for all chain configuration")]
        private Task InitBlockchain()
        {
            if (_api.ChainSpec == null) throw new StepDependencyException(nameof(_api.ChainSpec));
            if (_api.DbProvider == null) throw new StepDependencyException(nameof(_api.DbProvider));
            if (_api.SpecProvider == null) throw new StepDependencyException(nameof(_api.SpecProvider));

            ILogger logger = _api.LogManager.GetClassLogger();
            IInitConfig initConfig = _api.Config<IInitConfig>();
            ISyncConfig syncConfig = _api.Config<ISyncConfig>();
            if (syncConfig.DownloadReceiptsInFastSync && !syncConfig.DownloadBodiesInFastSync)
            {
                logger.Warn($"{nameof(syncConfig.DownloadReceiptsInFastSync)} is selected but {nameof(syncConfig.DownloadBodiesInFastSync)} - enabling bodies to support receipts download.");
                syncConfig.DownloadBodiesInFastSync = true;
            }
            
            Account.AccountStartNonce = _api.ChainSpec.Parameters.AccountStartNonce;
            
            Signer signer = new Signer(_api.SpecProvider.ChainId, _api.OriginalSignerKey, _api.LogManager);
            _api.Signer = signer;
            _api.SignerStore = signer;

            _api.StateProvider = new StateProvider(
                _api.DbProvider.StateDb,
                _api.DbProvider.CodeDb,
                _api.LogManager);

            _api.EthereumEcdsa = new EthereumEcdsa(_api.SpecProvider.ChainId, _api.LogManager);
            _api.TxPool = new TxPool.TxPool(
                new PersistentTxStorage(_api.DbProvider.PendingTxsDb),
                Timestamper.Default,
                _api.EthereumEcdsa,
                _api.SpecProvider,
                _api.Config<ITxPoolConfig>(),
                _api.StateProvider,
                _api.LogManager);
            
            TxSealer standardSealer = new TxSealer(_api.Signer, _api.Timestamper);
            NonceReservingTxSealer nonceReservingTxSealer =
                new NonceReservingTxSealer(_api.Signer, _api.Timestamper, _api.TxPool);

            if (_api.Config<IVaultConfig>().Enabled)
            {
                _api.TxSender = new TxPoolSender(_api.TxPool, standardSealer, nonceReservingTxSealer);
            }
            else
            {
                _api.TxSender = new VaultTxSender(_api.Config<IVaultConfig>());
            }

            IBloomConfig? bloomConfig = _api.Config<IBloomConfig>();

            IFileStoreFactory fileStoreFactory = initConfig.DiagnosticMode == DiagnosticMode.MemDb
                ? (IFileStoreFactory) new InMemoryDictionaryFileStoreFactory()
                : new FixedSizeFileStoreFactory(Path.Combine(initConfig.BaseDbPath, DbNames.Bloom), DbNames.Bloom, Bloom.ByteLength);

            _api.BloomStorage = bloomConfig.Index
                ? new BloomStorage(bloomConfig, _api.DbProvider.BloomDb, fileStoreFactory)
                : (IBloomStorage) NullBloomStorage.Instance;

            _api.DisposeStack.Push(_api.BloomStorage);

            _api.ChainLevelInfoRepository = new ChainLevelInfoRepository(_api.DbProvider.BlockInfosDb);

            _api.BlockTree = new BlockTree(
                _api.DbProvider.BlocksDb,
                _api.DbProvider.HeadersDb,
                _api.DbProvider.BlockInfosDb,
                _api.ChainLevelInfoRepository,
                _api.SpecProvider,
                _api.TxPool,
                _api.BloomStorage,
                _api.Config<ISyncConfig>(),
                _api.LogManager);

            // Init state if we need system calls before actual processing starts
            if (_api.BlockTree.Head != null)
            {
                _api.StateProvider.StateRoot = _api.BlockTree.Head.StateRoot;
            }

            _api.ReceiptStorage = initConfig.StoreReceipts ? (IReceiptStorage?) new PersistentReceiptStorage(_api.DbProvider.ReceiptsDb, _api.SpecProvider, new ReceiptsRecovery()) : NullReceiptStorage.Instance;
            _api.ReceiptFinder = new FullInfoReceiptFinder(_api.ReceiptStorage, new ReceiptsRecovery(), _api.BlockTree);

            _api.RecoveryStep = new TxSignaturesRecoveryStep(_api.EthereumEcdsa, _api.TxPool, _api.LogManager);

            _api.StorageProvider = new StorageProvider(
                _api.DbProvider.StateDb,
                _api.StateProvider,
                _api.LogManager);

            // blockchain processing
            BlockhashProvider blockhashProvider = new BlockhashProvider(
                _api.BlockTree, _api.LogManager);

            VirtualMachine virtualMachine = new VirtualMachine(
                _api.StateProvider,
                _api.StorageProvider,
                blockhashProvider,
                _api.SpecProvider,
                _api.LogManager);

            _api.TransactionProcessor = new TransactionProcessor(
                _api.SpecProvider,
                _api.StateProvider,
                _api.StorageProvider,
                virtualMachine,
                _api.LogManager);

            InitSealEngine();
            if (_api.SealValidator == null) throw new StepDependencyException(nameof(_api.SealValidator));

            /* validation */
            _api.HeaderValidator = CreateHeaderValidator();

            OmmersValidator ommersValidator = new OmmersValidator(
                _api.BlockTree,
                _api.HeaderValidator,
                _api.LogManager);

            TxValidator txValidator = new TxValidator(_api.SpecProvider.ChainId);

            _api.BlockValidator = new BlockValidator(
                txValidator,
                _api.HeaderValidator,
                ommersValidator,
                _api.SpecProvider,
                _api.LogManager);

            ReadOnlyDbProvider readOnly = new ReadOnlyDbProvider(_api.DbProvider, false);
            StateReader stateReader = new StateReader(readOnly.StateDb, readOnly.CodeDb, _api.LogManager);
            _api.TxPoolInfoProvider = new TxPoolInfoProvider(stateReader, _api.TxPool);

            _api.MainBlockProcessor = CreateBlockProcessor();

            BlockchainProcessor blockchainProcessor = new BlockchainProcessor(
                _api.BlockTree,
                _api.MainBlockProcessor,
                _api.RecoveryStep,
                _api.LogManager,
                new BlockchainProcessor.Options
                {
                    AutoProcess = !syncConfig.BeamSync,
                    StoreReceiptsByDefault = initConfig.StoreReceipts,
                });

            _api.BlockProcessingQueue = blockchainProcessor;
            _api.BlockchainProcessor = blockchainProcessor;

            if (syncConfig.BeamSync)
            {
                BeamBlockchainProcessor beamBlockchainProcessor = new BeamBlockchainProcessor(
                    new ReadOnlyDbProvider(_api.DbProvider, false),
                    _api.BlockTree,
                    _api.SpecProvider,
                    _api.LogManager,
                    _api.BlockValidator,
                    _api.RecoveryStep,
                    _api.RewardCalculatorSource!,
                    _api.BlockProcessingQueue,
                    _api.SyncModeSelector!);
                
                _api.DisposeStack.Push(beamBlockchainProcessor);
            }

            return Task.CompletedTask;
        }

        protected virtual  HeaderValidator CreateHeaderValidator() =>
            new HeaderValidator(
                _api.BlockTree,
                _api.SealValidator,
                _api.SpecProvider,
                _api.LogManager);

        protected virtual BlockProcessor CreateBlockProcessor()
        {
            if (_api.DbProvider == null) throw new StepDependencyException(nameof(_api.DbProvider));
            if (_api.RewardCalculatorSource == null) throw new StepDependencyException(nameof(_api.RewardCalculatorSource));

            return new BlockProcessor(
                _api.SpecProvider,
                _api.BlockValidator,
                _api.RewardCalculatorSource.Get(_api.TransactionProcessor),
                _api.TransactionProcessor,
                _api.DbProvider.StateDb,
                _api.DbProvider.CodeDb,
                _api.StateProvider,
                _api.StorageProvider,
                _api.TxPool,
                _api.ReceiptStorage,
                _api.LogManager);
        }

        protected virtual void InitSealEngine()
        {
            _api.Sealer = NullSealEngine.Instance;
            _api.SealValidator = NullSealEngine.Instance;
            _api.RewardCalculatorSource = NoBlockRewards.Instance;
        }
    }
}
