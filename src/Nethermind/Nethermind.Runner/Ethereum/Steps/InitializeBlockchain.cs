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
using Nethermind.Crypto;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Forks;
using Nethermind.Db;
using Nethermind.Db.Config;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Mining;
using Nethermind.Mining.Difficulty;
using Nethermind.PubSub;
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
            _context.SpecProvider = new ChainSpecBasedSpecProvider(_context.ChainSpec);

            Account.AccountStartNonce = _context.ChainSpec.Parameters.AccountStartNonce;

            /* sync */
            IDbConfig dbConfig = _context.ConfigProvider.GetConfig<IDbConfig>();
            _context.SyncConfig = _context.ConfigProvider.GetConfig<ISyncConfig>();

            foreach (PropertyInfo propertyInfo in typeof(IDbConfig).GetProperties())
            {
                if (_context.Logger.IsDebug) _context.Logger.Debug($"DB {propertyInfo.Name}: {propertyInfo.GetValue(dbConfig)}");
            }

            if (_context.SyncConfig.BeamSyncEnabled)
            {
                _context.DbProvider = new BeamSyncDbProvider(_context.InitConfig.BaseDbPath, dbConfig, _context.LogManager, _context.InitConfig.StoreReceipts || _context.SyncConfig.DownloadReceiptsInFastSync);
            }
            else
            {
                _context.DbProvider = _context.InitConfig.UseMemDb
                    ? (IDbProvider) new MemDbProvider()
                    : new RocksDbProvider(_context.InitConfig.BaseDbPath, dbConfig, _context.LogManager, _context.InitConfig.StoreReceipts || _context.SyncConfig.DownloadReceiptsInFastSync);
            }
            
            _context.DisposeStack.Push(_context.DbProvider);

            // IDbProvider debugRecorder = new RocksDbProvider(Path.Combine(_context._initConfig.BaseDbPath, "debug"), dbConfig, _context._logManager, _context._initConfig.StoreTraces, _context._initConfig.StoreReceipts);
            // _context._dbProvider = new RpcDbProvider(_context._jsonSerializer, new BasicJsonRpcClient(KnownRpcUris.Localhost, _context._jsonSerializer, _context._logManager), _context._logManager, debugRecorder);

            // IDbProvider debugReader = new ReadOnlyDbProvider(new RocksDbProvider(Path.Combine(_context._initConfig.BaseDbPath, "debug"), dbConfig, _context._logManager, _context._initConfig.StoreTraces, _context._initConfig.StoreReceipts), false);
            // _context._dbProvider = debugReader;

            _context.StateProvider = new StateProvider(
                _context.DbProvider.StateDb,
                _context.DbProvider.CodeDb,
                _context.LogManager);

            _context.EthereumEcdsa = new EthereumEcdsa(_context.SpecProvider, _context.LogManager);
            _context.TxPool = new TxPool(
                new PersistentTxStorage(_context.DbProvider.PendingTxsDb, _context.SpecProvider),
                Timestamper.Default,
                _context.EthereumEcdsa,
                _context.SpecProvider,
                _context.Config<ITxPoolConfig>(),
                _context.StateProvider,
                _context.LogManager);

            _context.ReceiptStorage = new PersistentReceiptStorage(_context.DbProvider.ReceiptsDb, _context.SpecProvider, _context.LogManager);

            _context.ChainLevelInfoRepository = new ChainLevelInfoRepository(_context.DbProvider.BlockInfosDb);

            _context.BlockTree = new BlockTree(
                _context.DbProvider.BlocksDb,
                _context.DbProvider.HeadersDb,
                _context.DbProvider.BlockInfosDb,
                _context.ChainLevelInfoRepository,
                _context.SpecProvider,
                _context.TxPool,
                _context.SyncConfig,
                _context.LogManager);

            // Init state if we need system calls before actual processing starts
            if (_context.BlockTree.Head != null)
            {
                _context.StateProvider.StateRoot = _context.BlockTree.Head.StateRoot;
            }

            _context.RecoveryStep = new TxSignaturesRecoveryStep(_context.EthereumEcdsa, _context.TxPool, _context.LogManager);

            _context.SnapshotManager = null;


            _context.StorageProvider = new StorageProvider(
                _context.DbProvider.StateDb,
                _context.StateProvider,
                _context.LogManager);

            IList<IAdditionalBlockProcessor> additionalBlockProcessors = new List<IAdditionalBlockProcessor>();
            // blockchain processing
            var blockhashProvider = new BlockhashProvider(
                _context.BlockTree, _context.LogManager);

            var virtualMachine = new VirtualMachine(
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

            InitSealEngine(additionalBlockProcessors);

            /* validation */
            _context.HeaderValidator = new HeaderValidator(
                _context.BlockTree,
                _context.SealValidator,
                _context.SpecProvider,
                _context.LogManager);

            var ommersValidator = new OmmersValidator(
                _context.BlockTree,
                _context.HeaderValidator,
                _context.LogManager);

            var txValidator = new TxValidator(_context.SpecProvider.ChainId);

            _context.BlockValidator = new BlockValidator(
                txValidator,
                _context.HeaderValidator,
                ommersValidator,
                _context.SpecProvider,
                _context.LogManager);

            _context.TxPoolInfoProvider = new TxPoolInfoProvider(_context.StateProvider, _context.TxPool);

            _context.BlockProcessor = new BlockProcessor(
                _context.SpecProvider,
                _context.BlockValidator,
                _context.RewardCalculator,
                _context.TransactionProcessor,
                _context.DbProvider.StateDb,
                _context.DbProvider.CodeDb,
                _context.StateProvider,
                _context.StorageProvider,
                _context.TxPool,
                _context.ReceiptStorage,
                _context.LogManager,
                additionalBlockProcessors);

            _context.BlockchainProcessor = new BlockchainProcessor(
                _context.BlockTree,
                _context.BlockProcessor,
                _context.RecoveryStep,
                _context.LogManager,
                _context.InitConfig.StoreReceipts);

            _context.FinalizationManager = InitFinalizationManager(additionalBlockProcessors);

            // create shared objects between discovery and peer manager
            IStatsConfig statsConfig = _context.ConfigProvider.GetConfig<IStatsConfig>();
            _context.NodeStatsManager = new NodeStatsManager(statsConfig, _context.LogManager);

            _context.BlockchainProcessor.Start();
            LoadGenesisBlock(string.IsNullOrWhiteSpace(_context.InitConfig.GenesisHash) ? null : new Keccak(_context.InitConfig.GenesisHash));
            
            ISubscription subscription;
            if (_context.Producers.Any())
            {
                subscription = new Subscription(_context.Producers, _context.BlockProcessor, _context.LogManager);
            }
            else
            {
                subscription = new EmptySubscription();
            }

            _context.DisposeStack.Push(subscription);

            return Task.CompletedTask;
        }
        
        private void InitSealEngine(IList<IAdditionalBlockProcessor> blockPreProcessors)
        {
            switch (_context.ChainSpec.SealEngineType)
            {
                case SealEngineType.None:
                    _context.Sealer = NullSealEngine.Instance;
                    _context.SealValidator = NullSealEngine.Instance;
                    _context.RewardCalculator = NoBlockRewards.Instance;
                    break;
                case SealEngineType.Clique:
                    _context.RewardCalculator = NoBlockRewards.Instance;
                    CliqueConfig cliqueConfig = new CliqueConfig();
                    cliqueConfig.BlockPeriod = _context.ChainSpec.Clique.Period;
                    cliqueConfig.Epoch = _context.ChainSpec.Clique.Epoch;
                    _context.SnapshotManager = new SnapshotManager(cliqueConfig, _context.DbProvider.BlocksDb, _context.BlockTree, _context.EthereumEcdsa, _context.LogManager);
                    _context.SealValidator = new CliqueSealValidator(cliqueConfig, _context.SnapshotManager, _context.LogManager);
                    _context.RecoveryStep = new CompositeDataRecoveryStep(_context.RecoveryStep, new AuthorRecoveryStep(_context.SnapshotManager));
                    if (_context.InitConfig.IsMining)
                    {
                        _context.Sealer = new CliqueSealer(new BasicWallet(_context.NodeKey), cliqueConfig, _context.SnapshotManager, _context.NodeKey.Address, _context.LogManager);
                    }
                    else
                    {
                        _context.Sealer = NullSealEngine.Instance;
                    }

                    break;
                case SealEngineType.NethDev:
                    _context.Sealer = NullSealEngine.Instance;
                    _context.SealValidator = NullSealEngine.Instance;
                    _context.RewardCalculator = NoBlockRewards.Instance;
                    break;
                case SealEngineType.Ethash:
                    _context.RewardCalculator = new RewardCalculator(_context.SpecProvider);
                    var difficultyCalculator = new DifficultyCalculator(_context.SpecProvider);
                    if (_context.InitConfig.IsMining)
                    {
                        _context.Sealer = new EthashSealer(new Ethash(_context.LogManager), _context.LogManager);
                    }
                    else
                    {
                        _context.Sealer = NullSealEngine.Instance;
                    }

                    _context.SealValidator = new EthashSealValidator(_context.LogManager, difficultyCalculator, _context.CryptoRandom, new Ethash(_context.LogManager));
                    break;
                case SealEngineType.AuRa:
                    var abiEncoder = new AbiEncoder();
                    var validatorProcessor = new AuRaAdditionalBlockProcessorFactory(_context.DbProvider.StateDb, _context.StateProvider, abiEncoder, _context.TransactionProcessor, _context.BlockTree, _context.ReceiptStorage, _context.LogManager)
                        .CreateValidatorProcessor(_context.ChainSpec.AuRa.Validators);

                    var auRaStepCalculator = new AuRaStepCalculator(_context.ChainSpec.AuRa.StepDuration, _context.Timestamper);
                    _context.SealValidator = new AuRaSealValidator(_context.ChainSpec.AuRa, auRaStepCalculator, validatorProcessor, _context.EthereumEcdsa, _context.LogManager);
                    _context.RewardCalculator = new AuRaRewardCalculator(_context.ChainSpec.AuRa, abiEncoder, _context.TransactionProcessor);
                    _context.Sealer = new AuRaSealer(_context.BlockTree, validatorProcessor, auRaStepCalculator, _context.NodeKey.Address, new BasicWallet(_context.NodeKey), _context.LogManager);
                    blockPreProcessors.Add(validatorProcessor);
                    break;
                default:
                    throw new NotSupportedException($"Seal engine type {_context.ChainSpec.SealEngineType} is not supported in Nethermind");
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

            Block genesis = _context.ChainSpec.Genesis;
            CreateSystemAccounts();

            foreach ((Address address, ChainSpecAllocation allocation) in _context.ChainSpec.Allocations)
            {
                _context.StateProvider.CreateAccount(address, allocation.Balance);
                if (allocation.Code != null)
                {
                    Keccak codeHash = _context.StateProvider.UpdateCode(allocation.Code);
                    _context.StateProvider.UpdateCodeHash(address, codeHash, _context.SpecProvider.GenesisSpec);
                }

                if (allocation.Constructor != null)
                {
                    Transaction constructorTransaction = new Transaction(true)
                    {
                        SenderAddress = address,
                        Init = allocation.Constructor,
                        GasLimit = genesis.GasLimit
                    };
                    _context.TransactionProcessor.Execute(constructorTransaction, genesis.Header, NullTxTracer.Instance);
                }
            }

            _context.StorageProvider.Commit();
            _context.StateProvider.Commit(_context.SpecProvider.GenesisSpec);

            _context.StorageProvider.CommitTrees();
            _context.StateProvider.CommitTree();

            _context.DbProvider.StateDb.Commit();
            _context.DbProvider.CodeDb.Commit();

            genesis.StateRoot = _context.StateProvider.StateRoot;
            genesis.Hash = genesis.Header.CalculateHash();

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
            var isAura = _context.ChainSpec.SealEngineType == SealEngineType.AuRa;
            var hasConstructorAllocation = _context.ChainSpec.Allocations.Values.Any(a => a.Constructor != null);
            if (isAura && hasConstructorAllocation)
            {
                _context.StateProvider.CreateAccount(Address.Zero, UInt256.Zero);
                _context.StorageProvider.Commit();
                _context.StateProvider.Commit(Homestead.Instance);
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
                if (_context.Logger.IsWarn) _context.Logger.Warn(_context.StateProvider.DumpState());
                if (_context.Logger.IsWarn) _context.Logger.Warn(_context.BlockTree.Genesis.ToString(BlockHeader.Format.Full));
                if (_context.Logger.IsError) _context.Logger.Error($"Unexpected genesis hash, expected {expectedGenesisHash}, but was {_context.BlockTree.Genesis.Hash}");
            }
            else
            {
                if (_context.Logger.IsDebug) _context.Logger.Debug($"Genesis hash :  {_context.BlockTree.Genesis.Hash}");
                ThisNodeInfo.AddInfo("Genesis hash :", $"{_context.BlockTree.Genesis.Hash}");
            }
        }
        
        private IBlockFinalizationManager InitFinalizationManager(IList<IAdditionalBlockProcessor> blockPreProcessors)
        {
            switch (_context.ChainSpec.SealEngineType)
            {
                case SealEngineType.AuRa:
                    return new AuRaBlockFinalizationManager(_context.BlockTree, _context.ChainLevelInfoRepository, _context.BlockProcessor,
                        blockPreProcessors.OfType<IAuRaValidator>().First(), _context.LogManager);
                default:
                    return null;
            }
        }
    }
}