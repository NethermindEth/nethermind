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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Abi;
using Nethermind.AuRa;
using Nethermind.AuRa.Rewards;
using Nethermind.AuRa.Validators;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.TxPools;
using Nethermind.Blockchain.TxPools.Storages;
using Nethermind.Blockchain.Validators;
using Nethermind.Clique;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs.ChainSpecStyle;
using Nethermind.Core.Specs.Forks;
using Nethermind.Db;
using Nethermind.Db.Config;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Mining;
using Nethermind.Mining.Difficulty;
using Nethermind.PubSub;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Forks;
using Nethermind.Stats;
using Nethermind.Store;
using Nethermind.Store.Repositories;
using Nethermind.Wallet;

namespace Nethermind.Runner.Ethereum.Steps
{
    [RunnerStepDependency(typeof(InitRlp), typeof(LoadChainspec))]
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
            _context.SpecProvider = new ChainSpecBasedSpecProvider(_context._chainSpec);

            Account.AccountStartNonce = _context._chainSpec.Parameters.AccountStartNonce;

            /* sync */
            IDbConfig dbConfig = _context._configProvider.GetConfig<IDbConfig>();
            _context._syncConfig = _context._configProvider.GetConfig<ISyncConfig>();

            foreach (PropertyInfo propertyInfo in typeof(IDbConfig).GetProperties())
            {
                if (_context.Logger.IsDebug) _context.Logger.Debug($"DB {propertyInfo.Name}: {propertyInfo.GetValue(dbConfig)}");
            }

            if (_context._syncConfig.BeamSyncEnabled)
            {
                _context._dbProvider = new BeamSyncDbProvider(_context._initConfig.BaseDbPath, dbConfig, _context.LogManager, _context._initConfig.StoreTraces, _context._initConfig.StoreReceipts || _context._syncConfig.DownloadReceiptsInFastSync);
            }
            else
            {
                _context._dbProvider = _context._initConfig.UseMemDb
                    ? (IDbProvider) new MemDbProvider()
                    : new RocksDbProvider(_context._initConfig.BaseDbPath, dbConfig, _context.LogManager, _context._initConfig.StoreTraces, _context._initConfig.StoreReceipts || _context._syncConfig.DownloadReceiptsInFastSync);
            }

            // IDbProvider debugRecorder = new RocksDbProvider(Path.Combine(_context._initConfig.BaseDbPath, "debug"), dbConfig, _context._logManager, _context._initConfig.StoreTraces, _context._initConfig.StoreReceipts);
            // _context._dbProvider = new RpcDbProvider(_context._jsonSerializer, new BasicJsonRpcClient(KnownRpcUris.Localhost, _context._jsonSerializer, _context._logManager), _context._logManager, debugRecorder);

            // IDbProvider debugReader = new ReadOnlyDbProvider(new RocksDbProvider(Path.Combine(_context._initConfig.BaseDbPath, "debug"), dbConfig, _context._logManager, _context._initConfig.StoreTraces, _context._initConfig.StoreReceipts), false);
            // _context._dbProvider = debugReader;

            _context._stateProvider = new StateProvider(
                _context._dbProvider.StateDb,
                _context._dbProvider.CodeDb,
                _context.LogManager);

            _context._ethereumEcdsa = new EthereumEcdsa(_context.SpecProvider, _context.LogManager);
            _context._txPool = new TxPool(
                new PersistentTxStorage(_context._dbProvider.PendingTxsDb, _context.SpecProvider),
                Timestamper.Default,
                _context._ethereumEcdsa,
                _context.SpecProvider,
                _context._txPoolConfig,
                _context._stateProvider,
                _context.LogManager);

            _context._receiptStorage = new PersistentReceiptStorage(_context._dbProvider.ReceiptsDb, _context.SpecProvider, _context.LogManager);

            _context._chainLevelInfoRepository = new ChainLevelInfoRepository(_context._dbProvider.BlockInfosDb);

            _context.BlockTree = new BlockTree(
                _context._dbProvider.BlocksDb,
                _context._dbProvider.HeadersDb,
                _context._dbProvider.BlockInfosDb,
                _context._chainLevelInfoRepository,
                _context.SpecProvider,
                _context._txPool,
                _context._syncConfig,
                _context.LogManager);

            // Init state if we need system calls before actual processing starts
            if (_context.BlockTree.Head != null)
            {
                _context._stateProvider.StateRoot = _context.BlockTree.Head.StateRoot;
            }

            _context._recoveryStep = new TxSignaturesRecoveryStep(_context._ethereumEcdsa, _context._txPool, _context.LogManager);

            _context._snapshotManager = null;


            _context._storageProvider = new StorageProvider(
                _context._dbProvider.StateDb,
                _context._stateProvider,
                _context.LogManager);

            IList<IAdditionalBlockProcessor> additionalBlockProcessors = new List<IAdditionalBlockProcessor>();
            // blockchain processing
            var blockhashProvider = new BlockhashProvider(
                _context.BlockTree, _context.LogManager);

            var virtualMachine = new VirtualMachine(
                _context._stateProvider,
                _context._storageProvider,
                blockhashProvider,
                _context.SpecProvider,
                _context.LogManager);

            _context._transactionProcessor = new TransactionProcessor(
                _context.SpecProvider,
                _context._stateProvider,
                _context._storageProvider,
                virtualMachine,
                _context.LogManager);

            InitSealEngine(additionalBlockProcessors);

            /* validation */
            _context._headerValidator = new HeaderValidator(
                _context.BlockTree,
                _context._sealValidator,
                _context.SpecProvider,
                _context.LogManager);

            var ommersValidator = new OmmersValidator(
                _context.BlockTree,
                _context._headerValidator,
                _context.LogManager);

            var txValidator = new TxValidator(_context.SpecProvider.ChainId);

            _context._blockValidator = new BlockValidator(
                txValidator,
                _context._headerValidator,
                ommersValidator,
                _context.SpecProvider,
                _context.LogManager);

            _context._txPoolInfoProvider = new TxPoolInfoProvider(_context._stateProvider, _context._txPool);

            _context._blockProcessor = new BlockProcessor(
                _context.SpecProvider,
                _context._blockValidator,
                _context._rewardCalculator,
                _context._transactionProcessor,
                _context._dbProvider.StateDb,
                _context._dbProvider.CodeDb,
                _context._dbProvider.TraceDb,
                _context._stateProvider,
                _context._storageProvider,
                _context._txPool,
                _context._receiptStorage,
                _context.LogManager,
                additionalBlockProcessors);

            _context._blockchainProcessor = new BlockchainProcessor(
                _context.BlockTree,
                _context._blockProcessor,
                _context._recoveryStep,
                _context.LogManager,
                _context._initConfig.StoreReceipts,
                _context._initConfig.StoreTraces);

            _context._finalizationManager = InitFinalizationManager(additionalBlockProcessors);

            // create shared objects between discovery and peer manager
            IStatsConfig statsConfig = _context._configProvider.GetConfig<IStatsConfig>();
            _context._nodeStatsManager = new NodeStatsManager(statsConfig, _context.LogManager);

            _context._blockchainProcessor.Start();
            LoadGenesisBlock(string.IsNullOrWhiteSpace(_context._initConfig.GenesisHash) ? null : new Keccak(_context._initConfig.GenesisHash));
            
            ISubscription subscription;
            if (_context.Producers.Any())
            {
                subscription = new Subscription(_context.Producers, _context._blockProcessor, _context.LogManager);
            }
            else
            {
                subscription = new EmptySubscription();
            }

            _context._disposeStack.Push(subscription);

            return Task.CompletedTask;
        }
        
        private void InitSealEngine(IList<IAdditionalBlockProcessor> blockPreProcessors)
        {
            switch (_context._chainSpec.SealEngineType)
            {
                case SealEngineType.None:
                    _context._sealer = NullSealEngine.Instance;
                    _context._sealValidator = NullSealEngine.Instance;
                    _context._rewardCalculator = NoBlockRewards.Instance;
                    break;
                case SealEngineType.Clique:
                    _context._rewardCalculator = NoBlockRewards.Instance;
                    CliqueConfig cliqueConfig = new CliqueConfig();
                    cliqueConfig.BlockPeriod = _context._chainSpec.Clique.Period;
                    cliqueConfig.Epoch = _context._chainSpec.Clique.Epoch;
                    _context._snapshotManager = new SnapshotManager(cliqueConfig, _context._dbProvider.BlocksDb, _context.BlockTree, _context._ethereumEcdsa, _context.LogManager);
                    _context._sealValidator = new CliqueSealValidator(cliqueConfig, _context._snapshotManager, _context.LogManager);
                    _context._recoveryStep = new CompositeDataRecoveryStep(_context._recoveryStep, new AuthorRecoveryStep(_context._snapshotManager));
                    if (_context._initConfig.IsMining)
                    {
                        _context._sealer = new CliqueSealer(new BasicWallet(_context._nodeKey), cliqueConfig, _context._snapshotManager, _context._nodeKey.Address, _context.LogManager);
                    }
                    else
                    {
                        _context._sealer = NullSealEngine.Instance;
                    }

                    break;
                case SealEngineType.NethDev:
                    _context._sealer = NullSealEngine.Instance;
                    _context._sealValidator = NullSealEngine.Instance;
                    _context._rewardCalculator = NoBlockRewards.Instance;
                    break;
                case SealEngineType.Ethash:
                    _context._rewardCalculator = new RewardCalculator(_context.SpecProvider);
                    var difficultyCalculator = new DifficultyCalculator(_context.SpecProvider);
                    if (_context._initConfig.IsMining)
                    {
                        _context._sealer = new EthashSealer(new Ethash(_context.LogManager), _context.LogManager);
                    }
                    else
                    {
                        _context._sealer = NullSealEngine.Instance;
                    }

                    _context._sealValidator = new EthashSealValidator(_context.LogManager, difficultyCalculator, _context._cryptoRandom, new Ethash(_context.LogManager));
                    break;
                case SealEngineType.AuRa:
                    var abiEncoder = new AbiEncoder();
                    var validatorProcessor = new AuRaAdditionalBlockProcessorFactory(_context._dbProvider.StateDb, _context._stateProvider, abiEncoder, _context._transactionProcessor, _context.BlockTree, _context._receiptStorage, _context.LogManager)
                        .CreateValidatorProcessor(_context._chainSpec.AuRa.Validators);

                    var auRaStepCalculator = new AuRaStepCalculator(_context._chainSpec.AuRa.StepDuration, _context._timestamper);
                    _context._sealValidator = new AuRaSealValidator(_context._chainSpec.AuRa, auRaStepCalculator, validatorProcessor, _context._ethereumEcdsa, _context.LogManager);
                    _context._rewardCalculator = new AuRaRewardCalculator(_context._chainSpec.AuRa, abiEncoder, _context._transactionProcessor);
                    _context._sealer = new AuRaSealer(_context.BlockTree, validatorProcessor, auRaStepCalculator, _context._nodeKey.Address, new BasicWallet(_context._nodeKey), _context.LogManager);
                    blockPreProcessors.Add(validatorProcessor);
                    break;
                default:
                    throw new NotSupportedException($"Seal engine type {_context._chainSpec.SealEngineType} is not supported in Nethermind");
            }
        }

        private void LoadGenesisBlock(Keccak expectedGenesisHash)
        {
            // if we already have a database with blocks then we do not need to load genesis from spec
            if (_context.BlockTree.Genesis != null)
            {
                ValidateGenesisHash(expectedGenesisHash);
                return;
            }

            Block genesis = _context._chainSpec.Genesis;
            CreateSystemAccounts();

            foreach ((Address address, ChainSpecAllocation allocation) in _context._chainSpec.Allocations)
            {
                _context._stateProvider.CreateAccount(address, allocation.Balance);
                if (allocation.Code != null)
                {
                    Keccak codeHash = _context._stateProvider.UpdateCode(allocation.Code);
                    _context._stateProvider.UpdateCodeHash(address, codeHash, _context.SpecProvider.GenesisSpec);
                }

                if (allocation.Constructor != null)
                {
                    Transaction constructorTransaction = new Transaction(true)
                    {
                        SenderAddress = address,
                        Init = allocation.Constructor,
                        GasLimit = genesis.GasLimit
                    };
                    _context._transactionProcessor.Execute(constructorTransaction, genesis.Header, NullTxTracer.Instance);
                }
            }

            _context._storageProvider.Commit();
            _context._stateProvider.Commit(_context.SpecProvider.GenesisSpec);

            _context._storageProvider.CommitTrees();
            _context._stateProvider.CommitTree();

            _context._dbProvider.StateDb.Commit();
            _context._dbProvider.CodeDb.Commit();

            genesis.StateRoot = _context._stateProvider.StateRoot;
            genesis.Hash = BlockHeader.CalculateHash(genesis.Header);

            ManualResetEventSlim genesisProcessedEvent = new ManualResetEventSlim(false);

            bool genesisLoaded = false;

            void GenesisProcessed(object sender, BlockEventArgs args)
            {
                genesisLoaded = true;
                _context.BlockTree.NewHeadBlock -= GenesisProcessed;
                genesisProcessedEvent.Set();
            }

            _context.BlockTree.NewHeadBlock += GenesisProcessed;
            _context.BlockTree.SuggestBlock(genesis);
            genesisProcessedEvent.Wait(TimeSpan.FromSeconds(40));
            if (!genesisLoaded)
            {
                throw new BlockchainException("Genesis block processing failure");
            }

            ValidateGenesisHash(expectedGenesisHash);
        }

        private void CreateSystemAccounts()
        {
            var isAura = _context._chainSpec.SealEngineType == SealEngineType.AuRa;
            var hasConstructorAllocation = _context._chainSpec.Allocations.Values.Any(a => a.Constructor != null);
            if (isAura && hasConstructorAllocation)
            {
                _context._stateProvider.CreateAccount(Address.Zero, UInt256.Zero);
                _context._storageProvider.Commit();
                _context._stateProvider.Commit(Homestead.Instance);
            }
        }

        /// <summary>
        /// If <paramref name="expectedGenesisHash"/> is <value>null</value> then it means that we do not care about the genesis hash (e.g. in some quick testing of private chains)/>
        /// </summary>
        /// <param name="expectedGenesisHash"></param>
        private void ValidateGenesisHash(Keccak expectedGenesisHash)
        {
            if (expectedGenesisHash != null && _context.BlockTree.Genesis.Hash != expectedGenesisHash)
            {
                if (_context.Logger.IsWarn) _context.Logger.Warn(_context._stateProvider.DumpState());
                if (_context.Logger.IsWarn) _context.Logger.Warn(_context.BlockTree.Genesis.ToString(BlockHeader.Format.Full));
                if (_context.Logger.IsError) _context.Logger.Error($"Unexpected genesis hash, expected {expectedGenesisHash}, but was {_context.BlockTree.Genesis.Hash}");
            }
            else
            {
                if (_context.Logger.IsInfo) _context.Logger.Info($"Genesis hash :  {_context.BlockTree.Genesis.Hash}");
            }
        }
        
        private IBlockFinalizationManager InitFinalizationManager(IList<IAdditionalBlockProcessor> blockPreProcessors)
        {
            switch (_context._chainSpec.SealEngineType)
            {
                case SealEngineType.AuRa:
                    return new AuRaBlockFinalizationManager(_context.BlockTree, _context._chainLevelInfoRepository, _context._blockProcessor,
                        blockPreProcessors.OfType<IAuRaValidator>().First(), _context.LogManager);
                default:
                    return null;
            }
        }
    }
}