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
using Nethermind.Runner.Ethereum.Context;
using Nethermind.State;
using Nethermind.State.Repositories;
using Nethermind.Db.Blooms;
using Nethermind.Synchronization.BeamSync;
using Nethermind.TxPool;
using Nethermind.TxPool.Storages;

namespace Nethermind.Runner.Ethereum.Steps
{
    [RunnerStepDependencies(typeof(InitRlp), typeof(InitDatabase), typeof(SetupKeyStore))]
    public class InitializeBlockchain : IStep
    {
        private readonly EthereumRunnerContext _context;

        public InitializeBlockchain(EthereumRunnerContext context)
        {
            _context = context;
        }

        public async Task Execute()
        {
            await InitBlockchain();
        }

        [Todo(Improve.Refactor, "Use chain spec for all chain configuration")]
        private Task InitBlockchain()
        {
            if (_context.ChainSpec == null) throw new StepDependencyException(nameof(_context.ChainSpec));
            if (_context.DbProvider == null) throw new StepDependencyException(nameof(_context.DbProvider));
            if (_context.SpecProvider == null) throw new StepDependencyException(nameof(_context.SpecProvider));

            ILogger logger = _context.LogManager.GetClassLogger();
            IInitConfig initConfig = _context.Config<IInitConfig>();
            ISyncConfig syncConfig = _context.Config<ISyncConfig>();
            if (syncConfig.DownloadReceiptsInFastSync && !syncConfig.DownloadBodiesInFastSync)
            {
                logger.Warn($"{nameof(syncConfig.DownloadReceiptsInFastSync)} is selected but {nameof(syncConfig.DownloadBodiesInFastSync)} - enabling bodies to support receipts download.");
                syncConfig.DownloadBodiesInFastSync = true;
            }
            
            Account.AccountStartNonce = _context.ChainSpec.Parameters.AccountStartNonce;

            _context.StateProvider = new StateProvider(
                _context.DbProvider.StateDb,
                _context.DbProvider.CodeDb,
                _context.LogManager);

            _context.EthereumEcdsa = new EthereumEcdsa(_context.SpecProvider.ChainId, _context.LogManager);
            _context.TxPool = new TxPool.TxPool(
                new PersistentTxStorage(_context.DbProvider.PendingTxsDb),
                Timestamper.Default,
                _context.EthereumEcdsa,
                _context.SpecProvider,
                _context.Config<ITxPoolConfig>(),
                _context.StateProvider,
                _context.LogManager);

            var bloomConfig = _context.Config<IBloomConfig>();

            var fileStoreFactory = initConfig.DiagnosticMode == DiagnosticMode.MemDb
                ? (IFileStoreFactory) new InMemoryDictionaryFileStoreFactory()
                : new FixedSizeFileStoreFactory(Path.Combine(initConfig.BaseDbPath, DbNames.Bloom), DbNames.Bloom, Bloom.ByteLength);

            _context.BloomStorage = bloomConfig.Index
                ? new BloomStorage(bloomConfig, _context.DbProvider.BloomDb, fileStoreFactory)
                : (IBloomStorage) NullBloomStorage.Instance;

            _context.DisposeStack.Push(_context.BloomStorage);

            _context.ChainLevelInfoRepository = new ChainLevelInfoRepository(_context.DbProvider.BlockInfosDb);

            _context.BlockTree = new BlockTree(
                _context.DbProvider.BlocksDb,
                _context.DbProvider.HeadersDb,
                _context.DbProvider.BlockInfosDb,
                _context.ChainLevelInfoRepository,
                _context.SpecProvider,
                _context.TxPool,
                _context.BloomStorage,
                _context.Config<ISyncConfig>(),
                _context.LogManager);

            // Init state if we need system calls before actual processing starts
            if (_context.BlockTree.Head != null)
            {
                _context.StateProvider.StateRoot = _context.BlockTree.Head.StateRoot;
            }

            _context.ReceiptStorage = initConfig.StoreReceipts ? (IReceiptStorage?) new PersistentReceiptStorage(_context.DbProvider.ReceiptsDb, _context.SpecProvider, new ReceiptsRecovery()) : NullReceiptStorage.Instance;
            _context.ReceiptFinder = new FullInfoReceiptFinder(_context.ReceiptStorage, new ReceiptsRecovery(), _context.BlockTree);

            _context.RecoveryStep = new TxSignaturesRecoveryStep(_context.EthereumEcdsa, _context.TxPool, _context.LogManager);

            _context.StorageProvider = new StorageProvider(
                _context.DbProvider.StateDb,
                _context.StateProvider,
                _context.LogManager);

            // blockchain processing
            BlockhashProvider blockhashProvider = new BlockhashProvider(
                _context.BlockTree, _context.LogManager);

            VirtualMachine virtualMachine = new VirtualMachine(
                _context.StateProvider,
                _context.StorageProvider,
                blockhashProvider,
                _context.SpecProvider,
                _context.LogManager);

            _context.TransactionProcessor = new TransactionProcessor(
                _context.SpecProvider,
                _context.StateProvider,
                _context.StorageProvider,
                virtualMachine,
                _context.LogManager);

            InitSealEngine();
            if (_context.SealValidator == null) throw new StepDependencyException(nameof(_context.SealValidator));

            /* validation */
            _context.HeaderValidator = CreateHeaderValidator();

            OmmersValidator ommersValidator = new OmmersValidator(
                _context.BlockTree,
                _context.HeaderValidator,
                _context.LogManager);

            TxValidator txValidator = new TxValidator(_context.SpecProvider.ChainId);

            _context.BlockValidator = new BlockValidator(
                txValidator,
                _context.HeaderValidator,
                ommersValidator,
                _context.SpecProvider,
                _context.LogManager);

            ReadOnlyDbProvider readOnly = new ReadOnlyDbProvider(_context.DbProvider, false);
            StateReader stateReader = new StateReader(readOnly.StateDb, readOnly.CodeDb, _context.LogManager);
            _context.TxPoolInfoProvider = new TxPoolInfoProvider(stateReader, _context.TxPool);

            _context.MainBlockProcessor = CreateBlockProcessor();

            BlockchainProcessor blockchainProcessor = new BlockchainProcessor(
                _context.BlockTree,
                _context.MainBlockProcessor,
                _context.RecoveryStep,
                _context.LogManager,
                initConfig.StoreReceipts,
                !syncConfig.BeamSync);

            _context.BlockProcessingQueue = blockchainProcessor;
            _context.BlockchainProcessor = blockchainProcessor;

            if (syncConfig.BeamSync)
            {
                BeamBlockchainProcessor beamBlockchainProcessor = new BeamBlockchainProcessor(
                    new ReadOnlyDbProvider(_context.DbProvider, false),
                    _context.BlockTree,
                    _context.SpecProvider,
                    _context.LogManager,
                    _context.BlockValidator,
                    _context.RecoveryStep,
                    _context.RewardCalculatorSource,
                    _context.BlockProcessingQueue,
                    _context.BlockchainProcessor,
                    _context.SyncModeSelector);
                
                _context.DisposeStack.Push(beamBlockchainProcessor);
            }

            ThisNodeInfo.AddInfo("Mem est trie :", $"{LruCache<Keccak, byte[]>.CalculateMemorySize(52 + 320, Trie.MemoryAllowance.TrieNodeCacheSize) / 1024 / 1024}MB".PadLeft(8));

            return Task.CompletedTask;
        }

        protected virtual  HeaderValidator CreateHeaderValidator() =>
            new HeaderValidator(
                _context.BlockTree,
                _context.SealValidator,
                _context.SpecProvider,
                _context.LogManager);

        protected virtual BlockProcessor CreateBlockProcessor()
        {
            if (_context.DbProvider == null) throw new StepDependencyException(nameof(_context.DbProvider));
            if (_context.RewardCalculatorSource == null) throw new StepDependencyException(nameof(_context.RewardCalculatorSource));

            return new BlockProcessor(
                _context.SpecProvider,
                _context.BlockValidator,
                _context.RewardCalculatorSource.Get(_context.TransactionProcessor),
                _context.TransactionProcessor,
                _context.DbProvider.StateDb,
                _context.DbProvider.CodeDb,
                _context.StateProvider,
                _context.StorageProvider,
                _context.TxPool,
                _context.ReceiptStorage,
                _context.LogManager);
        }

        protected virtual void InitSealEngine()
        {
            _context.Sealer = NullSealEngine.Instance;
            _context.SealValidator = NullSealEngine.Instance;
            _context.RewardCalculatorSource = NoBlockRewards.Source;
        }
    }
}