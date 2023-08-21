// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Validators;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Db.Blooms;
using Nethermind.Db.Rocks;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.State;
using Nethermind.State.Repositories;
using Nethermind.TxPool;
using Nethermind.Verkle.Tree;
using Nethermind.Verkle.Tree.VerkleDb;
using BlockTree = Nethermind.Blockchain.BlockTree;

namespace Nethermind.Core.Test.Blockchain;

public class TestVerkleBlockchain
{
    public const int DefaultTimeout = 400000;
    public const int PrivateKeyPoolCount = 10000;

    public PrivateKey[] RandomAddressWithBalance;

    public Random random = new Random(0);

    public VerkleStateReader StateReader { get; private set; } = null!;
    public IEthereumEcdsa EthereumEcdsa { get; private set; } = null!;
    public INonceManager NonceManager { get; private set; } = null!;
    public TransactionProcessor TxProcessor { get; set; } = null!;
    public IReceiptStorage ReceiptStorage { get; set; } = null!;
    public ITxPool TxPool { get; set; } = null!;
    public IDb CodeDb => DbProvider.CodeDb;
    public IBlockProcessor BlockProcessor { get; set; } = null!;
    public IBlockchainProcessor BlockchainProcessor { get; set; } = null!;

    public IBlockPreprocessorStep BlockPreprocessorStep { get; set; } = null!;

    public IBlockProcessingQueue BlockProcessingQueue { get; set; } = null!;
    public IBlockTree BlockTree { get; set; } = null!;

    public IBlockFinder BlockFinder
    {
        get => _blockFinder ?? BlockTree;
        set => _blockFinder = value;
    }

    public ILogFinder LogFinder { get; private set; } = null!;
    public IJsonSerializer JsonSerializer { get; set; } = null!;
    public IWorldState State { get; set; } = null!;
    public IReadOnlyStateProvider ReadOnlyState { get; private set; } = null!;
    public IDb StateDb => DbProvider.StateDb;
    public VerkleStateStore TrieStore { get; set; } = null!;
    public IBlockProducer BlockProducer { get; private set; } = null!;
    public IDbProvider DbProvider { get; set; } = null!;
    public ISpecProvider SpecProvider { get; set; } = null!;

    public ISealEngine SealEngine { get; set; } = null!;

    public ITransactionComparerProvider TransactionComparerProvider { get; set; } = null!;

    public IPoSSwitcher PoSSwitcher { get; set; } = null!;

    public static async Task<TestVerkleBlockchain> Create()
    {
        TestVerkleBlockchain chain = new ();
        await chain.Build();
        return chain;
    }

    public async Task BuildSomeBlocks(int numOfBlocks)
    {
        for (int i = 0; i < numOfBlocks; i++)
        {
            await AddBlock(GenerateRandomTxn(100));
        }
    }

    public async Task BuildSomeBlocksWithDeployment(int numOfBlocks)
    {
        for (int i = 0; i < numOfBlocks; i++)
        {
            await AddBlock(GenerateRandomTxnWithCode(5));
        }
    }

    public string SealEngineType { get; set; } = null!;


    public SemaphoreSlim _resetEvent = null!;
    private ManualResetEvent _suggestedBlockResetEvent = null!;
    private AutoResetEvent _oneAtATime = new(true);
    private IBlockFinder _blockFinder = null!;

    public static readonly UInt256 InitialValue = 100000.Ether();
    private TrieStoreBoundaryWatcher _trieStoreWatcher = null!;
    public IHeaderValidator HeaderValidator { get; set; } = null!;

    public IBlockValidator BlockValidator { get; set; } = null!;
    public BuildBlocksWhenRequested BlockProductionTrigger { get; } = new();

    public ReadOnlyVerkleStateStore ReadOnlyTrieStore { get; private set; } = null!;

    public ManualTimestamper Timestamper { get; protected set; } = null!;

    public ProducedBlockSuggester Suggester { get; protected set; } = null!;


    public static TransactionBuilder<Transaction> BuildSimpleTransaction => Builders.Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyA).To(TestItem.AddressB);

    public void GenerateRandomPrivateKeys(int numKeys)
    {
        RandomAddressWithBalance = new PrivateKey[numKeys];
        for (int i = 0; i < numKeys; i++) RandomAddressWithBalance[i] = new PrivateKey(TestItem.GetRandomKeccak(random).Bytes.ToArray());
    }

    public virtual async Task<TestVerkleBlockchain> Build(ISpecProvider? specProvider = null, UInt256? initialValues = null)
    {
        GenerateRandomPrivateKeys(PrivateKeyPoolCount);

        Timestamper = new ManualTimestamper(new DateTime(2020, 2, 15, 12, 50, 30, DateTimeKind.Utc));
        JsonSerializer = new EthereumJsonSerializer();
        SpecProvider = CreateSpecProvider(specProvider ?? new TestSpecProvider(Prague.Instance));
        EthereumEcdsa = new EthereumEcdsa(SpecProvider.ChainId, LogManager);

        DbProvider = CreateDbProvider();
        TrieStore = new VerkleStateStore(DbProvider, LogManager);
        byte[] code = Bytes.FromHexString("0xabcd");
        State = new VerkleWorldState(TrieStore, DbProvider.CodeDb, LogManager);
        State.CreateAccount(TestItem.AddressA, (initialValues ?? InitialValue));
        State.CreateAccount(TestItem.AddressB, (initialValues ?? InitialValue));
        State.CreateAccount(TestItem.AddressC, (initialValues ?? InitialValue));
        State.CreateAccount(TestItem.AddressD, (initialValues ?? InitialValue));
        State.CreateAccount(TestItem.AddressE, (initialValues ?? InitialValue));
        State.CreateAccount(TestItem.AddressF, (initialValues ?? InitialValue));
        State.InsertCode(TestItem.AddressA, code, SpecProvider.GenesisSpec);
        State.Set(new StorageCell(TestItem.AddressA, UInt256.One), Bytes.FromHexString("0xabcdef"));
        State.Commit(SpecProvider.GenesisSpec);
        State.CommitTree(0);

        ReadOnlyTrieStore = TrieStore.AsReadOnly(new VerkleMemoryDb());
        StateReader = new VerkleStateReader(ReadOnlyTrieStore, CodeDb, LogManager);

        IDb blockDb = new MemDb();
        IDb headerDb = new MemDb();
        IDb blockInfoDb = new MemDb();
        BlockTree = new BlockTree(blockDb, headerDb, blockInfoDb, new ChainLevelInfoRepository(blockInfoDb), SpecProvider, NullBloomStorage.Instance, SimpleConsoleLogManager.Instance);
        ReadOnlyState = new ChainHeadReadOnlyStateProvider(BlockTree, StateReader);
        TransactionComparerProvider = new TransactionComparerProvider(SpecProvider, BlockTree);
        TxPool = CreateTxPool();

        IChainHeadInfoProvider chainHeadInfoProvider =
            new ChainHeadInfoProvider(SpecProvider, BlockTree, StateReader);

        NonceManager = new NonceManager(chainHeadInfoProvider.AccountStateProvider);

        _trieStoreWatcher = new TrieStoreBoundaryWatcher(TrieStore, BlockTree, SimpleConsoleLogManager.Instance);

        ReceiptStorage = new InMemoryReceiptStorage();
        VirtualMachine virtualMachine = new(new BlockhashProvider(BlockTree, LogManager), SpecProvider, SimpleConsoleLogManager.Instance);
        TxProcessor = new TransactionProcessor(SpecProvider, State, virtualMachine, SimpleConsoleLogManager.Instance);
        BlockPreprocessorStep = new RecoverSignatures(EthereumEcdsa, TxPool, SpecProvider, LogManager);
        HeaderValidator = new HeaderValidator(BlockTree, Always.Valid, SpecProvider, LogManager);

        new ReceiptCanonicalityMonitor(BlockTree, ReceiptStorage, LogManager);

        BlockValidator = new BlockValidator(
            new TxValidator(SpecProvider.ChainId),
            HeaderValidator,
            Always.Valid,
            SpecProvider,
            LogManager);

        PoSSwitcher = NoPoS.Instance;
        ISealer sealer = new NethDevSealEngine(TestItem.AddressD);
        SealEngine = new SealEngine(sealer, Always.Valid);

        BloomStorage bloomStorage = new(new BloomConfig(), new MemDb(), new InMemoryDictionaryFileStoreFactory());
        ReceiptsRecovery receiptsRecovery = new(new EthereumEcdsa(SpecProvider.ChainId, SimpleConsoleLogManager.Instance), SpecProvider);
        LogFinder = new LogFinder(BlockTree, ReceiptStorage, ReceiptStorage, bloomStorage, SimpleConsoleLogManager.Instance, receiptsRecovery);
        BlockProcessor = CreateBlockProcessor();

        BlockchainProcessor chainProcessor = new(BlockTree, BlockProcessor, BlockPreprocessorStep, StateReader, SimpleConsoleLogManager.Instance, Consensus.Processing.BlockchainProcessor.Options.Default);
        BlockchainProcessor = chainProcessor;
        BlockProcessingQueue = chainProcessor;
        chainProcessor.Start();

        TxPoolTxSource txPoolTxSource = CreateTxPoolTxSource();
        ITransactionComparerProvider transactionComparerProvider = new TransactionComparerProvider(SpecProvider, BlockFinder);
        BlockProducer = CreateTestBlockProducer(txPoolTxSource, sealer, transactionComparerProvider);
        await BlockProducer.Start();
        Suggester = new ProducedBlockSuggester(BlockTree, BlockProducer);

        _resetEvent = new SemaphoreSlim(0);
        _suggestedBlockResetEvent = new ManualResetEvent(true);
        BlockTree.NewHeadBlock += OnNewHeadBlock;
        BlockProducer.BlockProduced += (s, e) =>
        {
            _suggestedBlockResetEvent.Set();
        };

        Block? genesis = GetGenesisBlock();
        BlockTree.SuggestBlock(genesis);

        await WaitAsync(_resetEvent, "Failed to process genesis in time.");
        await AirdropToAddresses();
        return this;
    }


    public Transaction[] GenerateRandomTxn(int numTxn)
    {
        Transaction[] txns = new Transaction[numTxn];

        for (int i = 0; i < numTxn; i++)
        {
            int fromTx = random.Next(RandomAddressWithBalance.Length - 1);
            int toTx = random.Next(RandomAddressWithBalance.Length - 1);
            if (fromTx == toTx) toTx = fromTx + 1;
            txns[i] = Builders.Build.A.Transaction.WithTo(RandomAddressWithBalance[fromTx].Address).WithValue(1.GWei())
                .SignedAndResolved(RandomAddressWithBalance[toTx]).TestObject;
        }

        return txns;
    }

    public Transaction[] GenerateRandomTxnWithCode(int numTxn)
    {
        List<Transaction> txns = new();
        int jump = RandomAddressWithBalance.Length / numTxn;
        int key = 0;
        while (key < RandomAddressWithBalance.Length)
        {
            byte[] code = new byte[TestItem.Random.Next(2000)];
            PrivateKey caller = RandomAddressWithBalance[key];
            UInt256 nonce = StateReader.GetNonce(BlockTree.Head!.StateRoot!, caller.Address);
            txns.Add(Builders.Build.A.Transaction.WithTo(null).WithCode(code).WithNonce(nonce).WithGasLimit(200000)
                .SignedAndResolved(caller).TestObject);
            key += jump;
        }
        return txns.ToArray();
    }

    protected virtual async Task AirdropToAddresses()
    {
        int batch = 10;

        List<Transaction> txToAdd = new();
        for (int i = 0; i < batch; i++)
        {
            txToAdd.Add(Builders.Build.A.Transaction.WithTo(RandomAddressWithBalance[i].Address).WithGasLimit(100000).WithMaxFeePerGas(20.GWei())
                .WithMaxPriorityFeePerGas(5.GWei())
                .WithType(TxType.EIP1559).WithValue(1.Ether()).WithNonce((UInt256)i)
                .SignedAndResolved(TestItem.PrivateKeyA).TestObject);
        }
        await AddBlock(txToAdd.ToArray());
        txToAdd.Clear();
        for (int i = batch; i < 2 * batch; i++)
        {
            txToAdd.Add(Builders.Build.A.Transaction.WithTo(RandomAddressWithBalance[i].Address).WithGasLimit(100000).WithMaxFeePerGas(30.GWei())
                .WithMaxPriorityFeePerGas(5.GWei())
                .WithType(TxType.EIP1559).WithValue(1.Ether()).WithNonce((UInt256)(i-batch))
                .SignedAndResolved(TestItem.PrivateKeyB).TestObject);
        }
        await AddBlock(txToAdd.ToArray());
        txToAdd.Clear();
        for (int i = 2 * batch; i < 3 * batch; i++)
        {
            txToAdd.Add(Builders.Build.A.Transaction.WithTo(RandomAddressWithBalance[i].Address).WithGasLimit(100000).WithMaxFeePerGas(40.GWei())
                .WithMaxPriorityFeePerGas(5.GWei())
                .WithType(TxType.EIP1559).WithValue(1.Ether()).WithNonce((UInt256)(i-2 * batch))
                .SignedAndResolved(TestItem.PrivateKeyC).TestObject);
        }
        await AddBlock(txToAdd.ToArray());
        txToAdd.Clear();
        for (int i = 3 * batch; i < 4 * batch; i++)
        {
            txToAdd.Add(Builders.Build.A.Transaction.WithTo(RandomAddressWithBalance[i].Address).WithGasLimit(100000).WithMaxFeePerGas(50.GWei())
                .WithMaxPriorityFeePerGas(5.GWei())
                .WithType(TxType.EIP1559).WithValue(1.Ether()).WithNonce((UInt256)(i-3 * batch))
                .SignedAndResolved(TestItem.PrivateKeyD).TestObject);
        }
        await AddBlock(txToAdd.ToArray());
        txToAdd.Clear();
        for (int i = 4 * batch; i < RandomAddressWithBalance.Length; i++)
        {
            txToAdd.Add(Builders.Build.A.Transaction.WithTo(RandomAddressWithBalance[i].Address).WithGasLimit(100000).WithMaxFeePerGas(60.GWei())
                .WithMaxPriorityFeePerGas(5.GWei())
                .WithType(TxType.EIP1559).WithValue(1.Ether()).WithNonce((UInt256)(i-4 * batch))
                .SignedAndResolved(TestItem.PrivateKeyE).TestObject);
        }
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

    protected virtual IDbProvider CreateDbProvider() => VerkleDbFactory.InitDatabase(DbMode.MemDb, null);

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
        BlocksConfig blocksConfig = new();

        BlockProducerEnvFactory blockProducerEnvFactory = new(
            DbProvider,
            BlockTree,
            ReadOnlyTrieStore,
            SpecProvider,
            BlockValidator,
            NoBlockRewards.Instance,
            ReceiptStorage,
            BlockPreprocessorStep,
            TxPool,
            transactionComparerProvider,
            blocksConfig,
            SimpleConsoleLogManager.Instance);

        BlockProducerEnv env = blockProducerEnvFactory.Create(txPoolTxSource);
        return new TestBlockProducer(
            env.TxSource,
            env.ChainProcessor,
            env.ReadOnlyStateProvider,
            sealer,
            BlockTree,
            BlockProductionTrigger,
            Timestamper,
            SpecProvider,
            SimpleConsoleLogManager.Instance,
            blocksConfig);
    }

    public virtual ILogManager LogManager { get; set; } = SimpleConsoleLogManager.Instance;

    protected virtual TxPool.TxPool CreateTxPool() =>
        new(
            EthereumEcdsa,
            new ChainHeadInfoProvider(new FixedForkActivationChainHeadSpecProvider(SpecProvider), BlockTree, ReadOnlyState),
            new TxPoolConfig(),
            new TxValidator(SpecProvider.ChainId),
            LimboLogs.Instance,
            TransactionComparerProvider.GetDefaultComparer());

    protected virtual TxPoolTxSource CreateTxPoolTxSource()
    {
        BlocksConfig blocksConfig = new()
        {
            MinGasPrice = 0
        };
        ITxFilterPipeline txFilterPipeline = TxFilterPipelineBuilder.CreateStandardFilteringPipeline(SimpleConsoleLogManager.Instance,
            SpecProvider, blocksConfig);
        return new TxPoolTxSource(TxPool, SpecProvider, TransactionComparerProvider, LogManager, txFilterPipeline);
    }

    public BlockBuilder GenesisBlockBuilder { get; set; } = null!;

    protected virtual Block GetGenesisBlock()
    {
        BlockBuilder genesisBlockBuilder = Builders.Build.A.Block.Genesis;
        if (GenesisBlockBuilder is not null)
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
        await AddBlock(BuildSimpleTransaction.WithNonce(0).TestObject);
        await AddBlock(BuildSimpleTransaction.WithNonce(1).TestObject, BuildSimpleTransaction.WithNonce(2).TestObject);
        await AddBlock(BuildSimpleTransaction.WithNonce(3).TestObject, BuildSimpleTransaction.WithNonce(4).TestObject);
    }

    protected virtual IBlockProcessor CreateBlockProcessor() =>
        new StatelessBlockProcessor(
            SpecProvider,
            BlockValidator,
            NoBlockRewards.Instance,
            new BlockProcessor.BlockStatelessValidationTransactionsExecutor(TxProcessor),
            State,
            ReceiptStorage,
            NullWitnessCollector.Instance,
            SimpleConsoleLogManager.Instance);

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
        BlockProducer?.StopAsync();
        if (DbProvider != null)
        {
            CodeDb?.Dispose();
            StateDb?.Dispose();
        }

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
        UInt256 nonce = StateReader.GetNonce(BlockTree.Head!.StateRoot!, TestItem.AddressA);
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
        UInt256 nonce = StateReader.GetNonce(BlockTree.Head!.StateRoot!, TestItem.AddressA);
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
