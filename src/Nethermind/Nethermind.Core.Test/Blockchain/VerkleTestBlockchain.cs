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

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Comparers;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Producers;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Validators;
using Nethermind.Consensus;
using Nethermind.Consensus.Transactions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Evm;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.State;
using Nethermind.State.Repositories;
using Nethermind.Db.Blooms;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Specs.Test;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using Nethermind.TxPool;
using NUnit.Framework;
using BlockTree = Nethermind.Blockchain.BlockTree;

namespace Nethermind.Core.Test.Blockchain
{
    public class VerkleTestBlockchain : IDisposable
    {
        public const int DefaultTimeout = 4000;
        public IStateReader StateReader { get; private set; }
        public IEthereumEcdsa EthereumEcdsa { get; private set; }
        public TransactionProcessor TxProcessor { get; set; }
        public IStorageProvider Storage { get; set; }
        public IReceiptStorage ReceiptStorage { get; set; }
        public ITxPool TxPool { get; set; }
        public IDb CodeDb => DbProvider.CodeDb;
        public IBlockProcessor BlockProcessor { get; set; }
        public IBlockchainProcessor BlockchainProcessor { get; set; }

        public IBlockPreprocessorStep BlockPreprocessorStep { get; set; }
        
        public IBlockProcessingQueue BlockProcessingQueue { get; set; }
        public IBlockTree BlockTree { get; set; }

        public IBlockFinder BlockFinder
        {
            get => _blockFinder ?? BlockTree;
            set => _blockFinder = value;
        }

        public IJsonSerializer JsonSerializer { get; set; }
        public IStateProvider State { get; set; }
        public IReadOnlyStateProvider ReadOnlyState { get; private set; }
        public IDb StateDb => DbProvider.StateDb;
        public VerkleTrieStore TrieStore { get; set; }
        public IBlockProducer BlockProducer { get; private set; }
        public IDbProvider DbProvider { get; set; }
        public ISpecProvider SpecProvider { get; set; }
        
        public ITransactionComparerProvider TransactionComparerProvider { get; set; }

        protected VerkleTestBlockchain()
        {
        }
        
        public string SealEngineType { get; set; }

        public static Address AccountA = TestItem.AddressA;
        public static Address AccountB = TestItem.AddressB;
        public static Address AccountC = TestItem.AddressC;
        public SemaphoreSlim _resetEvent;
        private ManualResetEvent _suggestedBlockResetEvent;
        private AutoResetEvent _oneAtATime = new(true);
        private IBlockFinder _blockFinder;

        public static readonly UInt256 InitialValue = 1000.Ether();
        private VerkleTrieStoreBoundaryWatcher _trieStoreWatcher;		
        private BlockValidator _blockValidator;
        public BuildBlocksWhenRequested BlockProductionTrigger { get; } = new();

        public IVerkleReadOnlyVerkleTrieStore ReadOnlyTrieStore { get; private set; }

        public ManualTimestamper Timestamper { get; protected set; }
        
        public ProducedBlockSuggester Suggester { get; private set; }

        public static TransactionBuilder<Transaction> BuildSimpleTransaction => Builders.Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyA).To(AccountB);

        protected virtual async Task<VerkleTestBlockchain> Build(ISpecProvider? specProvider = null, UInt256? initialValues = null)
        {
            Timestamper = new ManualTimestamper(new DateTime(2020, 2, 15, 12, 50, 30, DateTimeKind.Utc));
            JsonSerializer = new EthereumJsonSerializer();
            SpecProvider = CreateSpecProvider(specProvider ?? MainnetSpecProvider.Instance);
            EthereumEcdsa = new EthereumEcdsa(ChainId.Mainnet, LogManager);
            DbProvider = await CreateDbProvider();
            TrieStore = new VerkleTrieStore(DatabaseScheme.MemoryDb, LogManager);
            VerkleStateProvider stateProvider = new(TrieStore, LogManager, DbProvider.CodeDb);
            State = stateProvider;
            State.CreateAccount(TestItem.AddressA, (initialValues ?? InitialValue));
            State.CreateAccount(TestItem.AddressB, (initialValues ?? InitialValue));
            State.CreateAccount(TestItem.AddressC, (initialValues ?? InitialValue));
            byte[] code = Bytes.FromHexString("0xabcd");
            Keccak codeHash = Keccak.Compute(code);
            State.UpdateCode(code);
            State.UpdateCodeHash(TestItem.AddressA, codeHash, SpecProvider.GenesisSpec);

            Storage = new VerkleStorageProvider(stateProvider, LogManager);
            Storage.Set(new StorageCell(TestItem.AddressA, UInt256.One), Bytes.FromHexString("0xabcdef"));
            Storage.Commit();

            State.Commit(SpecProvider.GenesisSpec);
            State.CommitTree(0);
            
            ReadOnlyTrieStore = TrieStore.AsReadOnly();
            StateReader = new VerkleStateReader(ReadOnlyTrieStore, CodeDb, LogManager);
            
            IDb blockDb = new MemDb();
            IDb headerDb = new MemDb();
            IDb blockInfoDb = new MemDb();
            BlockTree = new BlockTree(blockDb, headerDb, blockInfoDb, new ChainLevelInfoRepository(blockInfoDb), SpecProvider, NullBloomStorage.Instance, LimboLogs.Instance);
            ReadOnlyState = new ChainHeadReadOnlyStateProvider(BlockTree, StateReader);
            TransactionComparerProvider = new TransactionComparerProvider(SpecProvider, BlockTree);
            TxPool = CreateTxPool();

            _trieStoreWatcher = new VerkleTrieStoreBoundaryWatcher(TrieStore, BlockTree, LogManager);
            

            ReceiptStorage = new InMemoryReceiptStorage();
            VirtualMachine virtualMachine = new(new BlockhashProvider(BlockTree, LogManager), SpecProvider, LogManager);
            TxProcessor = new TransactionProcessor(SpecProvider, State, Storage, virtualMachine, LogManager);
            BlockPreprocessorStep = new RecoverSignatures(EthereumEcdsa, TxPool, SpecProvider, LogManager);
            HeaderValidator headerValidator = new(BlockTree, Always.Valid, SpecProvider, LogManager);
                
            _blockValidator = new BlockValidator(
                new TxValidator(SpecProvider.ChainId),
                headerValidator,
                Always.Valid,
                SpecProvider,
                LogManager);
            
            BlockProcessor = CreateBlockProcessor();
            
            BlockchainProcessor chainProcessor = new(BlockTree, BlockProcessor, BlockPreprocessorStep, LogManager, Nethermind.Blockchain.Processing.BlockchainProcessor.Options.Default);
            BlockchainProcessor = chainProcessor;
            BlockProcessingQueue = chainProcessor;
            chainProcessor.Start();
            
            TxPoolTxSource txPoolTxSource = CreateTxPoolTxSource();
            ISealer sealer = new NethDevSealEngine(TestItem.AddressD);
            ITransactionComparerProvider transactionComparerProvider = new TransactionComparerProvider(SpecProvider, BlockFinder);
            BlockProducer = CreateTestBlockProducer(txPoolTxSource, sealer, transactionComparerProvider);
            Suggester = new ProducedBlockSuggester(BlockTree, BlockProducer);
            BlockProducer.Start();

            _resetEvent = new SemaphoreSlim(0);
            _suggestedBlockResetEvent = new ManualResetEvent(true);
            BlockTree.NewHeadBlock += OnNewHeadBlock;
            BlockProducer.BlockProduced += (s, e) =>
            {
                _suggestedBlockResetEvent.Set();
            };

            var genesis = GetGenesisBlock();
            BlockTree.SuggestBlock(genesis);
            await WaitAsync(_resetEvent, "Failed to process genesis in time.");
            await AddBlocksOnStart();
            return this;
        }

        private static ISpecProvider CreateSpecProvider(ISpecProvider specProvider)
        {
            return specProvider is TestSpecProvider { AllowTestChainOverride: false } 
                ? specProvider 
                : new OverridableSpecProvider(specProvider, s => new OverridableReleaseSpec(s) { IsEip3607Enabled = false });
        }

        private void OnNewHeadBlock(object? sender, BlockEventArgs e)
        {
            _resetEvent.Release(1);
        }

        protected virtual Task<IDbProvider> CreateDbProvider() => TestMemDbProvider.InitAsync();

        private async Task WaitAsync(SemaphoreSlim semaphore, string error, int timeout = DefaultTimeout)
        {
            if (!await semaphore.WaitAsync(timeout))
            {
                throw new InvalidOperationException(error);
            }
        }

        private async Task WaitAsync(EventWaitHandle eventWaitHandle, string error, int timeout = DefaultTimeout)
        {
            if (!await eventWaitHandle.WaitOneAsync(timeout, CancellationToken.None))
            {
                throw new InvalidOperationException(error);
            }
        }

        protected virtual IBlockProducer CreateTestBlockProducer(TxPoolTxSource txPoolTxSource, ISealer sealer, ITransactionComparerProvider transactionComparerProvider)
        {
            MiningConfig miningConfig = new();

            VerkleBlockProducerEnvFactory blockProducerEnvFactory = new(
                DbProvider, 
                BlockTree, 
                ReadOnlyTrieStore, 
                SpecProvider, 
                _blockValidator,
                NoBlockRewards.Instance,
                ReceiptStorage,
                BlockPreprocessorStep,
                TxPool,
                transactionComparerProvider,
                miningConfig,
                LogManager);

            BlockProducerEnv env = blockProducerEnvFactory.Create(txPoolTxSource);
            return new TestBlockProducer(env.TxSource, env.ChainProcessor, env.ReadOnlyStateProvider, sealer, BlockTree, BlockProductionTrigger, Timestamper, SpecProvider, LogManager);
        }

        public virtual ILogManager LogManager { get; } = LimboLogs.Instance;

        protected virtual TxPool.TxPool CreateTxPool() =>
            new(
                EthereumEcdsa,
                new ChainHeadInfoProvider(new FixedBlockChainHeadSpecProvider(SpecProvider), BlockTree, ReadOnlyState),
                new TxPoolConfig(),
                new TxValidator(SpecProvider.ChainId),
                LogManager,
                TransactionComparerProvider.GetDefaultComparer());

        protected virtual TxPoolTxSource CreateTxPoolTxSource()
        {
            ITxFilterPipeline txFilterPipeline = TxFilterPipelineBuilder.CreateStandardFilteringPipeline(LimboLogs.Instance,
                SpecProvider);
            return new TxPoolTxSource(TxPool, SpecProvider, TransactionComparerProvider, LogManager, txFilterPipeline);
        }

        public BlockBuilder GenesisBlockBuilder { get; set; }

        protected virtual Block GetGenesisBlock()
        {
            BlockBuilder genesisBlockBuilder = Builders.Build.A.Block.Genesis;
            if (GenesisBlockBuilder != null)
            {
                genesisBlockBuilder = GenesisBlockBuilder;
            }
            
            genesisBlockBuilder.WithStateRoot(State.StateRoot);
            if (SealEngineType == Nethermind.Core.SealEngineType.AuRa)
            {
                genesisBlockBuilder.WithAura(0, new byte[65]);
            }

            return genesisBlockBuilder.TestObject;
        }

        protected virtual async Task AddBlocksOnStart()
        {
            await AddBlock();
            await AddBlock(BuildSimpleTransaction.WithNonce(0).TestObject);
            await AddBlock(BuildSimpleTransaction.WithNonce(1).TestObject, BuildSimpleTransaction.WithNonce(2).TestObject);
        }

        protected virtual BlockProcessor CreateBlockProcessor() =>
            new(
                SpecProvider,
                _blockValidator,
                NoBlockRewards.Instance,
                new BlockProcessor.BlockValidationTransactionsExecutor(TxProcessor, State),
                State,
                Storage,
                ReceiptStorage,
                NullWitnessCollector.Instance,
                LogManager);

        public async Task WaitForNewHead()
        {
            await WaitAsync(_resetEvent, "Failed to produce new head in time.");
            _suggestedBlockResetEvent.Reset();
        }

        public async Task AddBlock(params Transaction[] transactions)
        {
            await AddBlockInternal(transactions);

            await WaitAsync(_resetEvent, "Failed to produce new head in time.");
            _suggestedBlockResetEvent.Reset();
            _oneAtATime.Set();
        }

        public async Task AddBlock(bool shouldWaitForHead = true, params Transaction[] transactions)
        {
            await AddBlockInternal(transactions);

            if (shouldWaitForHead)
            {
                await WaitAsync(_resetEvent, "Failed to produce new head in time.");
            }
            else
            {
                await WaitAsync(_suggestedBlockResetEvent, "Failed to produce new suggested block in time.");
            }

            _oneAtATime.Set();
        }

        private async Task<AcceptTxResult[]> AddBlockInternal(params Transaction[] transactions)
        {
            // we want it to be last event, so lets re-register
            BlockTree.NewHeadBlock -= OnNewHeadBlock;
            BlockTree.NewHeadBlock += OnNewHeadBlock;
            
            await WaitAsync(_oneAtATime, "Multiple block produced at once.");
            AcceptTxResult[] txResults = transactions.Select(t => TxPool.SubmitTx(t, TxHandlingOptions.None)).ToArray();
            Timestamper.Add(TimeSpan.FromSeconds(1));
            await BlockProductionTrigger.BuildBlock();
            return txResults;
        }

        public void AddTransactions(params Transaction[] txs)
        {
            for (int i = 0; i < txs.Length; i++)
            {
                TxPool.SubmitTx(txs[i], TxHandlingOptions.None);
            }
        }

        public virtual void Dispose()
        {
            TestContext.Out.WriteLine($"disposing {this.GetHashCode()}");
            BlockProducer?.StopAsync();
            CodeDb?.Dispose();
            StateDb?.Dispose();
            _trieStoreWatcher?.Dispose();
            DbProvider?.Dispose();
        }

        /// <summary>
        /// Creates a simple transfer transaction with value defined by <paramref name="ether"/>
        /// from a rich account to <paramref name="address"/>
        /// </summary>
        /// <param name="address">Address to add funds to</param>
        /// <param name="ether">Value of ether to add to the account</param>
        /// <returns></returns>
        public async Task AddFunds(Address address, UInt256 ether) =>
            await AddBlock(GetFundsTransaction(address, ether));

        public async Task AddFunds(params (Address address, UInt256 ether)[] funds) =>
            await AddBlock(funds.Select((f, i) => GetFundsTransaction(f.address, f.ether, (uint)i)).ToArray());
        
        public async Task AddFundsAfterLondon(params (Address address, UInt256 ether)[] funds) =>
            await AddBlock(funds.Select((f, i) => GetFunds1559Transaction(f.address, f.ether, (uint)i)).ToArray());
        
        private Transaction GetFundsTransaction(Address address, UInt256 ether, uint index = 0)
        {
            UInt256 nonce = StateReader.GetNonce(BlockTree.Head.StateRoot, TestItem.AddressA);
            Transaction tx = Builders.Build.A.Transaction
                .SignedAndResolved(TestItem.PrivateKeyA)
                .To(address)
                .WithNonce(nonce + index)
                .WithValue(ether)
                .TestObject;
            return tx;
        }
        
        private Transaction GetFunds1559Transaction(Address address, UInt256 ether, uint index = 0)
        {
            UInt256 nonce = StateReader.GetNonce(BlockTree.Head.StateRoot, TestItem.AddressA);
            Transaction tx = Builders.Build.A.Transaction
                .SignedAndResolved(TestItem.PrivateKeyA)
                .To(address)
                .WithNonce(nonce + index)
                .WithMaxFeePerGas(20.GWei())
                .WithMaxPriorityFeePerGas(5.GWei())
                .WithType(TxType.EIP1559)
                .WithValue(ether)
                .WithChainId(MainnetSpecProvider.Instance.ChainId)
                .TestObject;
            return tx;
        }
    }
}
