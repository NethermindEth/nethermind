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
using Nethermind.Crypto;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Evm;
using Nethermind.Mining;
using Nethermind.Mining.Difficulty;
using Nethermind.PubSub;
using Nethermind.Stats;
using Nethermind.Store;
using Nethermind.Store.Repositories;
using Nethermind.Wallet;

namespace Nethermind.Runner.Ethereum.Steps
{
    [RunnerStepDependency(typeof(InitRlp), typeof(LoadChainspec), typeof(InitDatabase))]
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
                _context.Config<ISyncConfig>(),
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

            IList<IAdditionalBlockProcessor> additionalBlockProcessors = new List<IAdditionalBlockProcessor>();
            InitSealEngine(additionalBlockProcessors);

            /* validation */
            _context.HeaderValidator = new HeaderValidator(
                _context.BlockTree,
                _context.SealValidator,
                _context.SpecProvider,
                _context.LogManager);

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

            BlockchainProcessor processor = new BlockchainProcessor(
                _context.BlockTree,
                _context.BlockProcessor,
                _context.RecoveryStep,
                _context.LogManager,
                _context.Config<IInitConfig>().StoreReceipts); 
            _context.BlockchainProcessor = processor;
            _context.BlockProcessingQueue = processor;

            _context.FinalizationManager = InitFinalizationManager(additionalBlockProcessors);

            // create shared objects between discovery and peer manager
            IStatsConfig statsConfig = _context.Config<IStatsConfig>();
            _context.NodeStatsManager = new NodeStatsManager(statsConfig, _context.LogManager);

            _context.BlockchainProcessor.Start();

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
                    if (_context.Config<IInitConfig>().IsMining)
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
                    DifficultyCalculator difficultyCalculator = new DifficultyCalculator(_context.SpecProvider);
                    if (_context.Config<IInitConfig>().IsMining)
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
                    AbiEncoder abiEncoder = new AbiEncoder();
                    _context.ValidatorStore = new ValidatorStore(_context.DbProvider.BlockInfosDb);
                    IAuRaValidatorProcessor validatorProcessor = new AuRaAdditionalBlockProcessorFactory(_context.StateProvider, abiEncoder, _context.TransactionProcessor, _context.BlockTree, _context.ReceiptStorage, _context.ValidatorStore, _context.LogManager)
                        .CreateValidatorProcessor(_context.ChainSpec.AuRa.Validators);
                    
                    AuRaStepCalculator auRaStepCalculator = new AuRaStepCalculator(_context.ChainSpec.AuRa.StepDuration, _context.Timestamper);    
                    _context.SealValidator = new AuRaSealValidator(_context.ChainSpec.AuRa, auRaStepCalculator, _context.ValidatorStore, _context.EthereumEcdsa, _context.LogManager);
                    _context.RewardCalculator = new AuRaRewardCalculator(_context.ChainSpec.AuRa, abiEncoder, _context.TransactionProcessor);
                    _context.Sealer = new AuRaSealer(_context.BlockTree, validatorProcessor, _context.ValidatorStore, auRaStepCalculator, _context.NodeKey.Address, new BasicWallet(_context.NodeKey), new ValidSealerStrategy(), _context.LogManager);
                    blockPreProcessors.Add(validatorProcessor);
                    break;
                default:
                    throw new NotSupportedException($"Seal engine type {_context.ChainSpec.SealEngineType} is not supported in Nethermind");
            }
        }
        
        private IBlockFinalizationManager InitFinalizationManager(IList<IAdditionalBlockProcessor> blockPreProcessors)
        {
            switch (_context.ChainSpec.SealEngineType)
            {
                case SealEngineType.AuRa:
                    AuRaBlockFinalizationManager finalizationManager = new AuRaBlockFinalizationManager(_context.BlockTree, _context.ChainLevelInfoRepository, _context.BlockProcessor, _context.ValidatorStore, new ValidSealerStrategy(), _context.LogManager);
                    foreach (IAuRaValidator auRaValidator in blockPreProcessors.OfType<IAuRaValidator>())
                    {
                        auRaValidator.SetFinalizationManager(finalizationManager);
                    }

                    return finalizationManager;
                default:
                    return null;
            }
        }
    }
}