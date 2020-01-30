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
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.TxPools;
using Nethermind.Blockchain.TxPools.Storages;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Mining;
using Nethermind.PubSub;
using Nethermind.Runner.Ethereum.Context;
using Nethermind.Stats;
using Nethermind.Store;
using Nethermind.Store.Repositories;

namespace Nethermind.Runner.Ethereum.Steps
{
    [RunnerStepDependency(typeof(InitRlp), typeof(InitDatabase), typeof(SetupKeyStore))]
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

            _context.BlockProcessor = CreateBlockProcessor();

            BlockchainProcessor processor = new BlockchainProcessor(
                _context.BlockTree,
                _context.BlockProcessor,
                _context.RecoveryStep,
                _context.LogManager,
                _context.Config<IInitConfig>().StoreReceipts); 
            _context.BlockchainProcessor = processor;
            _context.BlockProcessingQueue = processor;

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

        protected virtual BlockProcessor CreateBlockProcessor() =>
            new BlockProcessor(
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

        protected virtual void InitSealEngine()
        {
            _context.Sealer = NullSealEngine.Instance;
            _context.SealValidator = NullSealEngine.Instance;
            _context.RewardCalculatorSource = NoBlockRewards.Source;
        }       
    }
}