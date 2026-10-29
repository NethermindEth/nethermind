// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core.Messages;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Encoding;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.TxDecoders;
using Nethermind.Specs;
using NSubstitute;
using NUnit.Framework;
using Nethermind.Consensus;
using Nethermind.Consensus.Tracing;
using Nethermind.Core.Test.Modules;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Find;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Facade.Simulate;
using Nethermind.Core.Specs;
using Nethermind.Core.Precompiles;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.State.OverridableEnv;

namespace Nethermind.Facade.Test;

public class BlockchainBridgeTests
{
    private IBlockchainBridge _blockchainBridge;
    private IBlockTree _blockTree;
    private IReceiptStorage _receiptStorage;
    private ITransactionProcessor _transactionProcessor;
    private ManualTimestamper _timestamper;
    private ISpecProvider _specProvider;
    private IStateReader _stateReader;
    private IContainer _container;

    [SetUp]
    public Task SetUp()
    {
        _timestamper = new ManualTimestamper();
        _blockTree = Substitute.For<IBlockTree>();
        _receiptStorage = Substitute.For<IReceiptStorage>();
        _transactionProcessor = Substitute.For<ITransactionProcessor>();
        _stateReader = Substitute.For<IStateReader>();

        _stateReader.HasStateForBlock(Arg.Any<BlockHeader?>()).Returns(true);

        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule())
            .AddSingleton(_blockTree)
            .AddSingleton<IReceiptFinder>(_receiptStorage)
            .AddSingleton(_timestamper)
            .AddSingleton(Substitute.For<ILogFinder>())
            .AddSingleton<IMiningConfig>(new MiningConfig { Enabled = false })
            .AddScoped(_transactionProcessor)
            .AddSingleton(_stateReader)
            .Build();

        _specProvider = _container.Resolve<ISpecProvider>();
        _blockchainBridge = _container.Resolve<IBlockchainBridge>();
        return Task.CompletedTask;
    }

    [TearDown]
    public void TearDown() => _container.Dispose();

    [Test]
    public void get_transaction_returns_null_when_transaction_not_found()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_blockchainBridge.TryGetTransaction(TestItem.KeccakA, out TransactionLookupResult? result), Is.False);
            Assert.That(result, Is.Null);
        }
    }

    [Test]
    public void get_transaction_returns_null_when_block_not_found()
    {
        _receiptStorage.FindBlockHash(TestItem.KeccakA).Returns(TestItem.KeccakB);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_blockchainBridge.TryGetTransaction(TestItem.KeccakA, out TransactionLookupResult? result), Is.False);
            Assert.That(result, Is.Null);
        }
    }

    // regression test - was throwing NRE before
    // when receipt at txIndex had different TxHash
    [Test]
    public void get_transaction_returns_false_when_receipt_hash_mismatch()
    {
        Hash256 txHash = TestItem.KeccakA;
        Transaction tx = Build.A.Transaction.WithHash(txHash).TestObject;
        Block block = Build.A.Block.WithTransactions(tx).TestObject;
        TxReceipt receipt = Build.A.Receipt.WithTransactionHash(TestItem.KeccakC).TestObject;

        _receiptStorage.FindBlockHash(txHash).Returns(TestItem.KeccakB);
        _blockTree.FindBlock(TestItem.KeccakB, Arg.Any<BlockTreeLookupOptions>()).Returns(block);
        _receiptStorage.Get(block).Returns([receipt]);

        // checkTxnPool: false avoids falling through to the mempool path
        bool found = _blockchainBridge.TryGetTransaction(txHash, out TransactionLookupResult? result, checkTxnPool: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(found, Is.False);
            Assert.That(result, Is.Null);
        }
    }

    [Test]
    public void get_transaction_returns_receipt_and_transaction_when_found()
    {
        int index = 5;
        Transaction[] transactions = Enumerable.Range(0, 10)
            .Select(static i => Build.A.Transaction.WithNonce(i).WithHash(TestItem.Keccaks[i]).TestObject)
            .ToArray();
        Block block = Build.A.Block
            .WithTransactions(transactions.ToArray())
            .TestObject;
        TxReceipt[] receipts = block.Transactions.Select((t, i) => Build.A.Receipt
            .WithBlockHash(TestItem.KeccakB)
            .WithIndex(i)
            .WithTransactionHash(t.Hash)
            .WithSender(TestItem.Addresses[i])
            .TestObject
        ).ToArray();
        ;
        _blockTree.FindBlock(TestItem.KeccakB, Arg.Any<BlockTreeLookupOptions>()).Returns(block);
        foreach (TxReceipt receipt in receipts)
        {
            _receiptStorage.FindBlockHash(receipt.TxHash!).Returns(TestItem.KeccakB);
        }
        _receiptStorage.Get(block).Returns(receipts);
        Assert.That(_blockchainBridge.TryGetTransaction(transactions[index].Hash!, out TransactionLookupResult? result), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result!.Value.Transaction.Nonce, Is.EqualTo((ulong)index));
            Assert.That(result.Value.Transaction.Hash, Is.EqualTo(TestItem.Keccaks[index]));
            Assert.That(result.Value.Transaction.SenderAddress, Is.EqualTo(TestItem.Addresses[index]));
        }
        Assert.That(result.Value.ExtraData, Is.EqualTo(new TransactionForRpcContext(
            chainId: _specProvider.ChainId,
            blockHash: block.Hash,
            blockNumber: block.Number,
            txIndex: receipts[index].Index,
            blockTimestamp: block.Timestamp,
            baseFee: UInt256.Zero,
            receipt: receipts[index])));
    }

    private static IEnumerable<TestCaseData> StoredTransactionCases()
    {
        foreach (bool cachedBlock in new[] { false, true })
        {
            string suffix = cachedBlock ? " in the block cache" : " in the database";
            yield return new TestCaseData(Build.A.Transaction.WithTo(null).WithCode([0x60, 0x00]), StatusCode.Success, cachedBlock).SetName("Contract creation" + suffix);
            yield return new TestCaseData(Build.A.Transaction, StatusCode.Failure, cachedBlock).SetName("Failed legacy" + suffix);
            yield return new TestCaseData(Build.A.Transaction.WithType(TxType.AccessList).WithChainId(BlockchainIds.Mainnet)
                .WithAccessList(new AccessList.Builder().AddAddress(TestItem.AddressC).AddStorage(1).Build()), StatusCode.Success, cachedBlock).SetName("Access list" + suffix);
            yield return new TestCaseData(Build.A.Transaction.WithType(TxType.EIP1559).WithChainId(BlockchainIds.Mainnet).WithMaxFeePerGas(20).WithData([1, 2, 3]),
                StatusCode.Success, cachedBlock).SetName("EIP-1559" + suffix);
            yield return new TestCaseData(Build.A.Transaction.WithChainId(BlockchainIds.Mainnet).WithMaxFeePerGas(20).WithShardBlobTxTypeAndFields(2, isMempoolTx: false),
                StatusCode.Success, cachedBlock).SetName("Blob" + suffix);
            yield return new TestCaseData(Build.A.Transaction.WithType(TxType.SetCode).WithChainId(BlockchainIds.Mainnet).WithMaxFeePerGas(20)
                .WithAuthorizationCode(new AuthorizationTuple(BlockchainIds.Mainnet, TestItem.AddressC, 7, new Signature(new byte[64], 0))),
                StatusCode.Success, cachedBlock).SetName("Set code" + suffix);
        }
    }

    [TestCaseSource(nameof(StoredTransactionCases))]
    public void Transaction_lookup_from_the_stored_block_matches_the_full_block_lookup(TransactionBuilder<Transaction> target, byte status, bool cachedBlock)
    {
        using StoredChain chain = new();
        Transaction tx = target.WithNonce(1).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        Block block = chain.Add(tx, status, cachedBlock);

        // The first lookup reads the receipt from the database, the last one from the receipts cache the full lookup fills.
        (TxReceipt? storedReceipt, ulong storedTimestamp, TxGasInfo? storedGasInfo, int storedLogIndexStart) = chain.Bridge.GetTxReceiptInfo(tx.Hash!);
        Assert.That(storedReceipt, Is.Not.Null);
        chain.BlockTree.DidNotReceive().FindBlock(Arg.Any<Hash256>(), Arg.Any<BlockTreeLookupOptions>(), Arg.Any<ulong?>());

        chain.LookUpFullBlocks(true);
        (TxReceipt? fullReceipt, ulong fullTimestamp, TxGasInfo? fullGasInfo, int fullLogIndexStart) = chain.Bridge.GetTxReceiptInfo(tx.Hash!);
#pragma warning disable NUnit2045 // Keep output variables in the enclosing scope for subsequent checks and actions.
        Assert.That(fullReceipt, Is.Not.Null);
#pragma warning restore NUnit2045
        Assert.That(chain.Bridge.TryGetTransaction(tx.Hash!, out TransactionLookupResult? full, checkTxnPool: false), Is.True);
        chain.BlockTree.Received().FindBlock(block.Hash!, Arg.Any<BlockTreeLookupOptions>(), Arg.Any<ulong?>());

        chain.LookUpFullBlocks(false);
        Assert.That(chain.Bridge.TryGetTransaction(tx.Hash!, out TransactionLookupResult? stored, checkTxnPool: false), Is.True);
        chain.BlockTree.DidNotReceive().FindBlock(Arg.Any<Hash256>(), Arg.Any<BlockTreeLookupOptions>(), Arg.Any<ulong?>());

        Transaction storedTx = stored!.Value.Transaction!;
        Transaction fullTx = full!.Value.Transaction!;
        TransactionForRpcContext storedContext = stored.Value.ExtraData;
        TransactionForRpcContext fullContext = full.Value.ExtraData;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(storedTx.Hash, Is.EqualTo(tx.Hash));
            Assert.That(storedTx.Type, Is.EqualTo(tx.Type));
            Assert.That(storedTx.SenderAddress, Is.EqualTo(TestItem.AddressB));
            Assert.That(TxDecoder.Instance.Encode(storedTx).Bytes, Is.EqualTo(TxDecoder.Instance.Encode(fullTx).Bytes));
            Assert.That(storedContext.BlockHash, Is.EqualTo(fullContext.BlockHash));
            Assert.That(storedContext.BlockNumber, Is.EqualTo(fullContext.BlockNumber));
            Assert.That(storedContext.TxIndex, Is.EqualTo(1));
            Assert.That(storedContext.BlockTimestamp, Is.EqualTo(fullContext.BlockTimestamp));
            Assert.That(storedContext.BaseFee, Is.EqualTo(fullContext.BaseFee));
            Assert.That(storedTimestamp, Is.EqualTo(fullTimestamp));
            Assert.That(storedGasInfo, Is.EqualTo(fullGasInfo));
            Assert.That(storedLogIndexStart, Is.EqualTo(fullLogIndexStart));
        }

        storedContext.Receipt.AssertEquivalentTo(fullContext.Receipt);
        storedReceipt.AssertEquivalentTo(fullReceipt);
    }

    [Test]
    public void Transaction_lookup_fails_when_the_block_body_was_pruned()
    {
        using StoredChain chain = new();
        Transaction tx = Build.A.Transaction.WithNonce(1).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        Block block = chain.Add(tx, StatusCode.Success, cachedBlock: false);
        chain.BlockStore.Delete(block.Number, block.Hash!);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.Bridge.TryGetTransaction(tx.Hash!, out _, checkTxnPool: false), Is.False);
            Assert.That(chain.Bridge.GetTxReceiptInfo(tx.Hash!).Receipt, Is.Null);
        }
    }

    /// <summary>A bridge over real block and receipt stores, whose blocks hold a filler transaction before the one looked up.</summary>
    private sealed class StoredChain : IDisposable
    {
        private readonly ISpecProvider _specProvider = new TestSpecProvider(Prague.Instance);
        private readonly IContainer _container;
        private BlockHeader? _header;
        private readonly IReceiptStorage _writer;
        private bool _fullBlocks;

        public StoredChain()
        {
            BlockStore = new BlockStore(new MemDb());
            BlockTree = Substitute.For<IBlockTree>();
            BlockTree.FindHeader(Arg.Any<Hash256>(), Arg.Any<BlockTreeLookupOptions>(), Arg.Any<ulong?>())
                .Returns(call => !_fullBlocks && _header?.Hash == call.Arg<Hash256>() ? _header : null);
            BlockTree.FindBlock(Arg.Any<Hash256>(), Arg.Any<BlockTreeLookupOptions>(), Arg.Any<ulong?>())
                .Returns(call => _header?.Hash == call.Arg<Hash256>() ? BlockStore.Get(_header!.Number, _header.Hash!) : null);
            BlockTree.FindBlockHash(Arg.Any<ulong>()).Returns(call => _header?.Number == call.Arg<ulong>() ? _header.Hash : null);
            TestMemColumnsDb<ReceiptsColumns> receiptsDb = new();
            _writer = CreateReceiptStorage(receiptsDb);
            // Reads through another instance, whose receipts cache the writes did not fill.
            IReceiptStorage receiptStorage = CreateReceiptStorage(receiptsDb);
            _container = new ContainerBuilder()
                .AddModule(new TestNethermindModule())
                .AddSingleton(_specProvider)
                .AddSingleton(BlockTree)
                .AddSingleton<IBlockStore>(BlockStore)
                .AddSingleton<IReceiptFinder>(receiptStorage)
                .AddSingleton(Substitute.For<ILogFinder>())
                .AddSingleton<IMiningConfig>(new MiningConfig { Enabled = false })
                .AddSingleton(Substitute.For<IStateReader>())
                .Build();
            Bridge = _container.Resolve<IBlockchainBridge>();
        }

        public BlockStore BlockStore { get; }
        public IBlockTree BlockTree { get; }
        public IBlockchainBridge Bridge { get; }

        public Block Add(Transaction tx, byte status, bool cachedBlock)
        {
            Transaction filler = Build.A.Transaction.WithNonce(0).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
            Block block = Build.A.Block.WithNumber(1).WithBaseFeePerGas(7).WithExcessBlobGas(0).WithBlobGasUsed(0)
                .WithReceiptsRoot(TestItem.KeccakA).WithTransactions(filler, tx).TestObject;
            LogEntry log = new(TestItem.AddressC, [1], [TestItem.KeccakC]);
            TxReceipt[] receipts =
            [
                Build.A.Receipt.WithTransactionHash(filler.Hash).WithSender(TestItem.AddressB).WithLogs(log, log).WithGasUsedTotal(21_000).TestObject,
                Build.A.Receipt.WithTransactionHash(tx.Hash).WithSender(TestItem.AddressB).WithStatusCode(status)
                    .WithLogs(status == StatusCode.Success ? new[] { log } : Array.Empty<LogEntry>()).WithGasUsedTotal(70_000).TestObject,
            ];

            BlockStore.Insert(block);
            if (cachedBlock) BlockStore.Cache(block);
            _writer.Insert(block, receipts);
            _header = block.Header;
            return block;
        }

        /// <summary>Whether lookups take the whole block from the block tree instead of the stored block.</summary>
        public void LookUpFullBlocks(bool fullBlocks)
        {
            _fullBlocks = fullBlocks;
            BlockTree.ClearReceivedCalls();
        }

        private PersistentReceiptStorage CreateReceiptStorage(TestMemColumnsDb<ReceiptsColumns> receiptsDb) =>
            new(receiptsDb, _specProvider, new ReceiptsRecovery(new EthereumEcdsa(_specProvider.ChainId), _specProvider), BlockTree, BlockStore, new ReceiptConfig());

        public void Dispose() => _container.Dispose();
    }

    [Test]
    public void Call_uses_valid_post_merge_and_random_value()
    {
        BlockHeader header = Build.A.BlockHeader
            .WithDifficulty(0)
            .WithMixHash(TestItem.KeccakA)
            .TestObject;

        Transaction tx = Build.A.Transaction.TestObject;

        _blockchainBridge.Call(header, tx);
        _transactionProcessor.Received().SetBlockExecutionContext(Arg.Is<BlockExecutionContext>(static blkCtx =>
            blkCtx.Header.IsPostMerge && blkCtx.Header.Random == TestItem.KeccakA));
        _transactionProcessor.Received().CallAndRestore(
            tx,
            Arg.Any<ITxTracer>());
    }

    [Test]
    public void Call_leaves_the_hash_of_ethereum_types_null([Values(TxType.Legacy, TxType.AccessList, TxType.EIP1559, TxType.FrameTx)] TxType type)
    {
        Transaction tx = Build.A.Transaction.WithType(type).TestObject;
        tx.Hash = TestItem.KeccakA;

        _blockchainBridge.Call(Build.A.BlockHeader.TestObject, tx);

        Assert.That(tx.Hash, Is.Null);
    }

    [Test]
    public void Call_computes_the_hash_of_other_types()
    {
        // Stands in for a plugin's decoder: core registers none for deposit transactions.
        TxDecoder.Instance.RegisterDecoder(TxType.DepositTx, new EIP1559TxDecoder());
        try
        {
            Transaction tx = Build.A.Transaction.WithType(TxType.DepositTx).TestObject;
            tx.Hash = TestItem.KeccakA;

            _blockchainBridge.Call(Build.A.BlockHeader.TestObject, tx);

            Assert.That(tx.Hash, Is.EqualTo(tx.CalculateHash()));
        }
        finally
        {
            TxDecoder.Instance.RegisterDecoder(TxType.DepositTx, null!);
        }
    }

    [Test]
    public void Call_uses_valid_block_number()
    {
        _timestamper.UtcNow = DateTime.MinValue;
        _timestamper.Add(TimeSpan.FromDays(123));
        BlockHeader header = Build.A.BlockHeader.WithNumber(10).TestObject;
        Transaction tx = new() { GasLimit = Transaction.BaseTxGasCost };

        _blockchainBridge.Call(header, tx);
        _transactionProcessor.Received().SetBlockExecutionContext(
            Arg.Is<BlockExecutionContext>(static blkCtx => blkCtx.Header.Number == 10));
        _transactionProcessor.Received().CallAndRestore(
            tx,
            Arg.Any<ITxTracer>());
    }

    [Test]
    public void EstimateGas_executes_on_the_given_block_header()
    {
        _timestamper.UtcNow = DateTime.MinValue;
        _timestamper.Add(TimeSpan.FromDays(123));
        BlockHeader header = Build.A.BlockHeader.WithNumber(10).WithTimestamp(1_000).TestObject;
        Transaction tx = new() { GasLimit = Transaction.BaseTxGasCost };

        _blockchainBridge.EstimateGas(header, tx, 1);

        _transactionProcessor.Received().SetBlockExecutionContext(
            Arg.Is<BlockExecutionContext>(static blkCtx => blkCtx.Header.Number == 10 && blkCtx.Header.Timestamp == 1_000));
        _transactionProcessor.DidNotReceive().SetBlockExecutionContext(
            Arg.Is<BlockExecutionContext>(static blkCtx => blkCtx.Header.Number != 10));
    }

    [Test]
    public void EstimateGas_without_state_is_rejected_at_the_requested_gas_limit()
    {
        IShareableTxProcessorSource unavailableState = Substitute.For<IShareableTxProcessorSource>();
        unavailableState.TryBuild(Arg.Any<BlockHeader?>(), out Arg.Any<IReadOnlyTxProcessingScope?>()).Returns(false);
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule())
            .AddSingleton(_blockTree)
            .AddSingleton<IReceiptFinder>(_receiptStorage)
            .AddSingleton(Substitute.For<ILogFinder>())
            .AddSingleton<IMiningConfig>(new MiningConfig { Enabled = false })
            .AddSingleton(unavailableState)
            .Build();
        BlockHeader header = Build.A.BlockHeader.WithNumber(10).TestObject;
        Transaction tx = new() { GasLimit = 50_000 };

        CallOutput callOutput = container.Resolve<IBlockchainBridge>().EstimateGas(header, tx, 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(callOutput.Error, Does.StartWith("No state available for block"));
            Assert.That(callOutput.InputError, Is.True, "the RPC error names the gas limit");
            Assert.That(callOutput.GasSpent, Is.EqualTo(50_000ul), "the requested gas limit");
        }
    }

    [Test]
    public void Override_routing_cases_cover_all_block_fields() =>
        Assert.That(Enum.GetNames<OverrideInput>(), Is.SupersetOf(typeof(BlockOverride).GetProperties()
            .Where(static property => property.CanWrite)
            .Select(static property => property.Name)));

    [TestCaseSource(nameof(OverrideRoutingCases))]
    public void Override_processing_uses_exclusive_source_only_when_fields_are_supplied(OverrideCall call, OverrideInput input)
    {
        IShareableTxProcessorSource shared = Substitute.For<IShareableTxProcessorSource>();
        IShareableOverridableEnvSource<BlockchainBridge.BlockProcessingComponents> exclusive =
            Substitute.For<IShareableOverridableEnvSource<BlockchainBridge.BlockProcessingComponents>>();
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule())
            .AddSingleton(_blockTree)
            .AddSingleton<IReceiptFinder>(_receiptStorage)
            .AddSingleton(Substitute.For<ILogFinder>())
            .AddSingleton<IMiningConfig>(new MiningConfig { Enabled = false })
            .AddSingleton(shared)
            .AddSingleton(exclusive)
            .AddScoped<SimulateReadOnlyBlocksProcessingEnvPool>(ctx => new(ctx.Resolve<ISimulateReadOnlyBlocksProcessingEnvFactory>().Create, 1))
            .AddScoped<BlockchainBridge>()
            .Build();
        BlockOverride? blockOverride = input switch
        {
            OverrideInput.None or OverrideInput.State or OverrideInput.BlobFeeArgument => null,
            OverrideInput.Number => new() { Number = 0 },
            OverrideInput.PrevRandao => new() { PrevRandao = Hash256.Zero },
            OverrideInput.Time => new() { Time = 0 },
            OverrideInput.GasLimit => new() { GasLimit = 0 },
            OverrideInput.FeeRecipient => new() { FeeRecipient = Address.Zero },
            OverrideInput.BaseFeePerGas => new() { BaseFeePerGas = UInt256.Zero },
            OverrideInput.BlobBaseFee => new() { BlobBaseFee = UInt256.Zero },
            OverrideInput.BeaconRoot => new() { BeaconRoot = TestItem.KeccakA },
            OverrideInput.Withdrawals => new() { Withdrawals = [new Withdrawal { Address = TestItem.AddressA, AmountInGwei = 1 }] },
            _ => new(),
        };
        Dictionary<Address, AccountOverride>? stateOverride = input == OverrideInput.State
            ? new() { [TestItem.AddressA] = new() { Balance = UInt256.One } }
            : null;
        UInt256? blobBaseFeeOverride = input == OverrideInput.BlobFeeArgument ? UInt256.Zero : null;
        BlockHeader header = Build.A.BlockHeader.WithNumber(10).TestObject;
        Transaction tx = new() { GasLimit = 50_000 };
        IBlockchainBridge bridge = container.Resolve<BlockchainBridge>();

        string? error = call switch
        {
            OverrideCall.Call => bridge.Call(header, tx, stateOverride, blobBaseFeeOverride, blockOverride, cancellationToken: CancellationToken.None).Error,
            OverrideCall.EstimateGas => bridge.EstimateGas(header, tx, 1, stateOverride, blobBaseFeeOverride, blockOverride, 0, cancellationToken: CancellationToken.None).Error,
            OverrideCall.EstimateFrameGas => bridge.EstimateFrameGas(header, tx, [], [], 0, 1, null, stateOverride, blockOverride, CancellationToken.None, out _).Error,
            _ => throw new ArgumentOutOfRangeException(nameof(call))
        };

        bool unsupported = input is OverrideInput.BeaconRoot or OverrideInput.Withdrawals;
        using (Assert.EnterMultipleScope())
        {
            if (unsupported)
            {
                string field = input == OverrideInput.BeaconRoot ? "beaconRoot" : "withdrawals";
                Assert.That(error, Is.EqualTo($"block override \"{field}\" is not supported for this RPC method"));
            }
            bool hasAppliedOverrides = !unsupported && input is not (OverrideInput.None or OverrideInput.Empty);
            shared.Received(hasAppliedOverrides || unsupported ? 0 : 1).TryBuild(Arg.Any<BlockHeader?>(), out Arg.Any<IReadOnlyTxProcessingScope?>());
            exclusive.Received(hasAppliedOverrides ? 1 : 0).TryBuildAndOverride(
                Arg.Any<BlockHeader?>(), stateOverride, blockOverride, out Arg.Any<Scope<BlockchainBridge.BlockProcessingComponents>?>());
        }
    }

    [Test]
    public void Call_uses_valid_mix_hash()
    {
        _timestamper.UtcNow = DateTime.MinValue;
        _timestamper.Add(TimeSpan.FromDays(123));
        BlockHeader header = Build.A.BlockHeader.WithMixHash(TestItem.KeccakA).TestObject;
        Transaction tx = new() { GasLimit = Transaction.BaseTxGasCost };

        _blockchainBridge.Call(header, tx);
        _transactionProcessor.Received().SetBlockExecutionContext(
            Arg.Is<BlockExecutionContext>(static blkCtx => blkCtx.Header.MixHash == TestItem.KeccakA));
        _transactionProcessor.Received().CallAndRestore(
            tx,
            Arg.Any<ITxTracer>());
    }

    [Test]
    public void Call_uses_valid_beneficiary()
    {
        _timestamper.UtcNow = DateTime.MinValue;
        _timestamper.Add(TimeSpan.FromDays(123));
        BlockHeader header = Build.A.BlockHeader.WithBeneficiary(TestItem.AddressB).TestObject;
        Transaction tx = new() { GasLimit = Transaction.BaseTxGasCost };

        _blockchainBridge.Call(header, tx);
        _transactionProcessor.Received().SetBlockExecutionContext(
            Arg.Is<BlockExecutionContext>(static blkCtx => blkCtx.Header.Beneficiary == TestItem.AddressB));
        _transactionProcessor.Received().CallAndRestore(
            tx,
            Arg.Any<ITxTracer>());
    }

    [Test]
    public void Bridge_head_is_correct([Values(7UL, 0UL)] ulong headNumber)
    {
        Block head = Build.A.Block.WithNumber(headNumber).TestObject;
        Block bestSuggested = Build.A.Block.WithNumber(8UL).TestObject;

        _blockTree.Head.Returns(head);
        _blockTree.BestSuggestedBody.Returns(bestSuggested);

        Assert.That(_blockchainBridge.HeadBlock, Is.EqualTo(head));
    }

    [Test]
    public void GetReceiptAndGasInfo_returns_correct_results([Values] bool isCanonical, [Values] bool postEip4844)
    {
        Hash256 txHash = TestItem.KeccakA;
        Hash256 blockHash = TestItem.KeccakB;
        UInt256 effectiveGasPrice = 123;

        Transaction tx = postEip4844
            ? Build.A.Transaction
                .WithGasPrice(effectiveGasPrice)
                .WithMaxFeePerGas(effectiveGasPrice)
                .WithType(TxType.Blob)
                .WithMaxFeePerBlobGas(2)
                .WithBlobVersionedHashes(2)
                .WithHash(txHash)
                .TestObject
            : Build.A.Transaction
                .WithGasPrice(effectiveGasPrice)
                .WithMaxFeePerGas(effectiveGasPrice)
                .WithHash(txHash)
                .TestObject;
        Block block = postEip4844
            ? Build.A.Block
                .WithNumber(MainnetSpecProvider.ParisBlockNumber)
                .WithTimestamp(MainnetSpecProvider.CancunBlockTimestamp)
                .WithTransactions(tx)
                .WithExcessBlobGas(2)
                .TestObject
            : Build.A.Block
                .WithNumber(MainnetSpecProvider.ParisBlockNumber)
                .WithTimestamp(MainnetSpecProvider.CancunBlockTimestamp)
                .WithTransactions(tx)
                .TestObject;
        TxReceipt receipt = Build.A.Receipt
            .WithBlockHash(blockHash)
            .WithTransactionHash(txHash)
            .TestObject;

        _blockTree.FindBlock(blockHash, Arg.Is(BlockTreeLookupOptions.RequireCanonical)).Returns(isCanonical ? block : null);
        _blockTree.FindBlock(blockHash, Arg.Is(BlockTreeLookupOptions.TotalDifficultyNotNeeded)).Returns(block);
        _receiptStorage.FindBlockHash(txHash).Returns(blockHash);
        _receiptStorage.Get(block).Returns([receipt]);

        (TxReceipt? Receipt, ulong BlockTimestamp, TxGasInfo? GasInfo, int LogIndexStart) result = postEip4844
            ? (receipt, MainnetSpecProvider.CancunBlockTimestamp, new(effectiveGasPrice, 1, 262144), 0)
            : (receipt, MainnetSpecProvider.CancunBlockTimestamp, new(effectiveGasPrice), 0);

        if (!isCanonical)
        {
            result = (null, 0, null, 0);
        }

        Assert.That(_blockchainBridge.GetTxReceiptInfo(txHash), Is.EqualTo(result));
    }

    private static Action<IBlockchainBridge, BlockHeader, Transaction>[] BridgeCallSources() =>
    [
        .. CallSources(),
        (bridge, header, tx) => bridge.CreateAccessList(header, tx, null, false),
    ];

    [Test]
    public void CreateAccessList_sets_maxFeePerBlobGas()
    {
        _timestamper.UtcNow = DateTime.MaxValue;
        BlockHeader header = Build.A.BlockHeader
            .WithBeneficiary(TestItem.AddressB)
            .WithExcessBlobGas(0)
            .WithBlobGasUsed(0)
            .WithNumber(long.MaxValue)
            .WithTimestamp(ulong.MaxValue)
            .TestObject;
        Transaction tx = new() { Type = TxType.Blob, MaxFeePerBlobGas = null, BlobVersionedHashes = [] };

        _blockchainBridge.CreateAccessList(header, tx, null, false);
        _transactionProcessor.Received().CallAndRestore(
            Arg.Is<Transaction>(static tx => tx.MaxFeePerBlobGas == 1),
            Arg.Any<ITxTracer>());
    }

    private static Action<IBlockchainBridge, BlockHeader, Transaction>[] CallSources() =>
    [
        (bridge, header, tx) => bridge.Call(header, tx),
        (bridge, header, tx) => bridge.EstimateGas(header, tx, 1),
    ];

    private static IEnumerable<TestCaseData> ZeroBlobFeeCapCallCases()
    {
        Action<IBlockchainBridge, BlockHeader, Transaction>[] calls = CallSources();
        foreach (bool capOmitted in new[] { true, false })
        {
            yield return new TestCaseData(TxType.Blob, calls[0], capOmitted).SetArgDisplayNames(nameof(TxType.Blob), "Call", capOmitted.ToString());
            yield return new TestCaseData(TxType.Blob, calls[1], capOmitted).SetArgDisplayNames(nameof(TxType.Blob), "EstimateGas", capOmitted.ToString());
            yield return new TestCaseData(TxType.FrameTx, calls[0], capOmitted).SetArgDisplayNames(nameof(TxType.FrameTx), "Call", capOmitted.ToString());
        }
    }

    [TestCaseSource(nameof(ZeroBlobFeeCapCallCases))]
    public void Blob_call_without_a_positive_blob_fee_cap_runs_at_a_zero_blob_base_fee(
        TxType txType, Action<IBlockchainBridge, BlockHeader, Transaction> bridgeCall, bool capOmitted)
    {
        _timestamper.UtcNow = DateTime.MaxValue;
        BlockHeader header = Build.A.BlockHeader
            .WithBeneficiary(TestItem.AddressB)
            .WithExcessBlobGas(10_000_000)
            .WithBlobGasUsed(0)
            .WithNumber(long.MaxValue)
            .WithTimestamp(ulong.MaxValue)
            .TestObject;
        Transaction tx = new()
        {
            Type = txType,
            GasLimit = Transaction.BaseTxGasCost,
            MaxFeePerBlobGas = capOmitted ? null : UInt256.Zero,
            BlobVersionedHashes = [new byte[Eip4844Constants.BytesPerBlobVersionedHash]]
        };

        bridgeCall(_blockchainBridge, header, tx);

        Assert.That(tx.MaxFeePerBlobGas, Is.EqualTo(UInt256.Zero));
        _transactionProcessor.Received().SetBlockExecutionContext(
            Arg.Is<BlockExecutionContext>(static blkCtx => blkCtx.BlobBaseFee == UInt256.Zero.ToValueHash()));
    }

    [Test]
    public void Frame_blob_call_with_a_max_fee_runs_at_the_header_blob_base_fee([Values] bool capOmitted)
    {
        _timestamper.UtcNow = DateTime.MaxValue;
        const ulong excessBlobGas = 10_000_000;
        BlockHeader header = Build.A.BlockHeader
            .WithBeneficiary(TestItem.AddressB)
            .WithExcessBlobGas(excessBlobGas)
            .WithBlobGasUsed(0)
            .WithNumber(long.MaxValue)
            .WithTimestamp(ulong.MaxValue)
            .TestObject;
        UInt256? maxFeePerBlobGas = capOmitted ? null : UInt256.Zero;
        Transaction tx = new()
        {
            Type = TxType.FrameTx,
            GasLimit = Transaction.BaseTxGasCost,
            MaxFee = 1,
            MaxFeePerBlobGas = maxFeePerBlobGas,
            BlobVersionedHashes = [new byte[Eip4844Constants.BytesPerBlobVersionedHash]]
        };
        BlobGasCalculator.TryCalculateFeePerBlobGas(excessBlobGas, _specProvider.GetSpec(header).BlobBaseFeeUpdateFraction, out UInt256 blobBaseFee);
        ValueHash256 expectedBlobBaseFee = blobBaseFee.ToValueHash();

        _blockchainBridge.Call(header, tx);

        Assert.That(tx.MaxFeePerBlobGas, Is.EqualTo(maxFeePerBlobGas));
        _transactionProcessor.Received().SetBlockExecutionContext(
            Arg.Is<BlockExecutionContext>(blkCtx => blkCtx.BlobBaseFee == expectedBlobBaseFee));
    }

    [Test, Combinatorial]
    public void BlobBaseFee_is_set_for_non_blob_transaction([ValueSource(nameof(BridgeCallSources))] Action<IBlockchainBridge, BlockHeader, Transaction> bridgeCall, [Values(0ul, 100ul)] ulong excessBlobGas)
    {
        _timestamper.UtcNow = DateTime.MaxValue;
        BlockHeader header = Build.A.BlockHeader
            .WithBeneficiary(TestItem.AddressB)
            .WithExcessBlobGas(excessBlobGas)
            .WithBlobGasUsed(0)
            .WithNumber(long.MaxValue)
            .WithTimestamp(ulong.MaxValue)
            .TestObject;
        Transaction tx = new() { GasLimit = Transaction.BaseTxGasCost };

        IReleaseSpec spec = _specProvider.GetSpec(header);
        BlobGasCalculator.TryCalculateFeePerBlobGas(excessBlobGas, spec.BlobBaseFeeUpdateFraction, out UInt256 expectedBlobBaseFee);
        ValueHash256 expectedBlobBaseFeeHash = expectedBlobBaseFee.ToValueHash();

        bridgeCall(_blockchainBridge, header, tx);

        _transactionProcessor.Received().SetBlockExecutionContext(
            Arg.Is<BlockExecutionContext>(blkCtx => blkCtx.BlobBaseFee == expectedBlobBaseFeeHash));
    }

    private static Action<IBlockchainBridge, BlockHeader, Transaction, bool>[] NoBaseFeeCallSources() =>
    [
        static (bridge, header, tx, noBaseFee) => bridge.Call(header, tx, noBaseFee: noBaseFee),
        static (bridge, header, tx, noBaseFee) => bridge.EstimateGas(header, tx, 1, noBaseFee: noBaseFee),
        static (bridge, header, tx, noBaseFee) => bridge.CreateAccessList(header, tx, null, false, noBaseFee: noBaseFee),
    ];

    [Test, Combinatorial]
    public void Calls_run_on_a_copy_of_the_header([ValueSource(nameof(NoBaseFeeCallSources))] Action<IBlockchainBridge, BlockHeader, Transaction, bool> bridgeCall, [Values] bool noBaseFee)
    {
        _timestamper.UtcNow = DateTime.MaxValue;
        BlockHeader header = Build.A.BlockHeader
            .WithBeneficiary(TestItem.AddressB)
            .WithDifficulty(0)
            .WithGasUsed(21_000)
            .WithBaseFee(7)
            .WithExcessBlobGas(0)
            .WithBlobGasUsed(Eip4844Constants.GasPerBlob)
            .WithNumber(long.MaxValue)
            .WithTimestamp(ulong.MaxValue)
            .TestObject;
        BlockHeader snapshot = header.Clone();
        Transaction tx = new() { GasLimit = Transaction.BaseTxGasCost };

        bridgeCall(_blockchainBridge, header, tx, noBaseFee);

        UInt256 expectedBaseFee = noBaseFee ? UInt256.Zero : 7;
        _transactionProcessor.Received().SetBlockExecutionContext(Arg.Is<BlockExecutionContext>(blkCtx =>
            blkCtx.Header != header && blkCtx.Header.GasUsed == 0 && blkCtx.Header.BaseFeePerGas == expectedBaseFee
            && blkCtx.Header.BlobGasUsed == 0 && blkCtx.Header.IsPostMerge));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(header.GasUsed, Is.EqualTo(snapshot.GasUsed));
            Assert.That(header.BaseFeePerGas, Is.EqualTo(snapshot.BaseFeePerGas));
            Assert.That(header.BlobGasUsed, Is.EqualTo(snapshot.BlobGasUsed));
            Assert.That(header.IsPostMerge, Is.EqualTo(snapshot.IsPostMerge));
        }
    }

    [Test]
    public void CreateAccessList_filters_precompile_addresses_with_empty_storage_keys()
    {
        CallOutput callOutput = InvokeCreateAccessListWithMockedAccess(
            [PrecompiledAddresses.ECRecover, TestItem.AddressC],
            [new StorageCell(TestItem.AddressC, UInt256.One)]);

        Assert.That(callOutput.AccessList, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(callOutput.AccessList!.Any(e => e.Address == PrecompiledAddresses.ECRecover), Is.False);
            Assert.That(callOutput.AccessList.Any(e => e.Address == TestItem.AddressC), Is.True);
        }
    }

    [Test]
    public void CreateAccessList_keeps_precompile_address_when_storage_key_is_present()
    {
        CallOutput callOutput = InvokeCreateAccessListWithMockedAccess(
            [PrecompiledAddresses.ECRecover],
            [new StorageCell(PrecompiledAddresses.ECRecover, UInt256.One)]);

        Assert.That(callOutput.AccessList, Is.Not.Null);
        Assert.That(callOutput.AccessList!.Any(e => e.Address == PrecompiledAddresses.ECRecover), Is.True);
    }

    [TestCase(null, null)]
    [TestCase(null, "revert")]
    [TestCase(null, "some plain error string")]
    public void CreateAccessList_reverting_tx_returns_execution_reverted_error(byte[]? returnValue, string? tracerError)
    {
        BlockHeader header = Build.A.BlockHeader.TestObject;
        Transaction tx = Build.A.Transaction
            .WithSenderAddress(TestItem.AddressA)
            .WithTo(TestItem.AddressB)
            .TestObject;

        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<ITxTracer>())
            .Returns(callInfo =>
            {
                ITxTracer tracer = callInfo.ArgAt<ITxTracer>(1);
                tracer.ReportAction(tx.GasLimit, UInt256.Zero, TestItem.AddressA, TestItem.AddressB, ReadOnlyMemory<byte>.Empty, ExecutionType.TRANSACTION);
                tracer.ReportActionError(EvmExceptionType.Revert);
                tracer.MarkAsFailed(TestItem.AddressB, new GasConsumed(21000, 0), returnValue ?? [], tracerError);
                return TransactionResult.EvmException(EvmExceptionType.Revert);
            });

        CallOutput callOutput = _blockchainBridge.CreateAccessList(header, tx, null, false, null, cancellationToken: default);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(callOutput.Error, Is.EqualTo("execution reverted"));
            Assert.That(callOutput.ExecutionReverted, Is.True);
        }
    }

    private CallOutput InvokeCreateAccessListWithMockedAccess(
        Address[] accessedAddresses,
        StorageCell[] accessedCells)
    {
        BlockHeader header = Build.A.BlockHeader.TestObject;
        Transaction tx = Build.A.Transaction
            .WithSenderAddress(TestItem.AddressA)
            .WithTo(TestItem.AddressB)
            .TestObject;

        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<ITxTracer>())
            .Returns(callInfo =>
            {
                ITxTracer tracer = callInfo.ArgAt<ITxTracer>(1);
                tracer.ReportAccess(accessedAddresses, accessedCells);
                tracer.MarkAsSuccess(TestItem.AddressB, new GasConsumed(21000, 0), Array.Empty<byte>(), Array.Empty<LogEntry>());
                return TransactionResult.Ok;
            });

        return _blockchainBridge.CreateAccessList(header, tx, null, false, null, cancellationToken: default);
    }

    [Test]
    public void Call_tx_returns_InsufficientSenderBalanceError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new() { GasLimit = 100 };
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.InsufficientSenderBalance);

        CallOutput callOutput = _blockchainBridge.Call(header, tx);

        Assert.That(callOutput.Error, Is.EqualTo(GasEstimator.InsufficientBalance));
    }

    [Test]
    public void EstimateGas_tx_returns_InsufficientSenderBalanceError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new() { GasLimit = 100 };
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.InsufficientSenderBalance);

        CallOutput callOutput = _blockchainBridge.EstimateGas(header, tx, 1);

        Assert.That(callOutput.Error, Is.EqualTo(GasEstimator.InsufficientBalance));
    }

    [Test]
    public void Call_tx_returns_SenderNotSpecifiedError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new() { GasLimit = 123 };
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.SenderNotSpecified);

        CallOutput callOutput = _blockchainBridge.Call(header, tx);

        Assert.That(callOutput.Error, Is.EqualTo("sender not specified"));
    }

    [Test]
    public void EstimateGas_tx_returns_SenderNotSpecifiedError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new() { GasLimit = 123 };
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.SenderNotSpecified);

        CallOutput callOutput = _blockchainBridge.EstimateGas(header, tx, 1);

        Assert.That(callOutput.Error, Is.EqualTo("sender not specified"));
    }

    [Test]
    public void Call_tx_returns_MalformedTransactionError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new();
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.MalformedTransaction);

        CallOutput callOutput = _blockchainBridge.Call(header, tx);

        Assert.That(callOutput.Error, Is.EqualTo("malformed"));
    }

    [Test]
    public void EstimateGas_tx_returns_MalformedTransactionError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new();
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.MalformedTransaction);

        CallOutput callOutput = _blockchainBridge.EstimateGas(header, tx, 1);

        Assert.That(callOutput.Error, Is.EqualTo("malformed"));
    }

    [Test]
    public void Call_tx_returns_TransactionNonceIsToHighError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new() { GasLimit = 456 };
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.TransactionNonceTooHigh);

        CallOutput callOutput = _blockchainBridge.Call(header, tx);

        Assert.That(callOutput.Error, Is.EqualTo("nonce too high"));
    }

    [Test]
    public void Call_tx_returns_TransactionNonceIsToLowError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new() { GasLimit = 456 };
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.TransactionNonceTooLow);

        CallOutput callOutput = _blockchainBridge.Call(header, tx);

        Assert.That(callOutput.Error, Is.EqualTo("nonce too low"));
    }

    [Test]
    public void EstimateGas_tx_returns_TransactionNonceIsToHighError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new() { GasLimit = 456 };
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.TransactionNonceTooHigh);

        CallOutput callOutput = _blockchainBridge.EstimateGas(header, tx, 1);

        Assert.That(callOutput.Error, Is.EqualTo("nonce too high"));
    }

    [Test]
    public void EstimateGas_tx_returns_TransactionNonceIsTooLowError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new() { GasLimit = 456 };
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.TransactionNonceTooLow);

        CallOutput callOutput = _blockchainBridge.EstimateGas(header, tx, 1);

        Assert.That(callOutput.Error, Is.EqualTo("nonce too low"));
    }

    [Test]
    public void Call_tx_returns_NonceOverflowError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new() { GasLimit = 0 };
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.NonceOverflow);

        CallOutput callOutput = _blockchainBridge.Call(header, tx);

        Assert.That(callOutput.Error, Is.EqualTo("nonce overflow"));
    }

    [Test]
    public void EstimateGas_tx_returns_NonceOverflowError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new() { GasLimit = 0 };
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.NonceOverflow);

        CallOutput callOutput = _blockchainBridge.EstimateGas(header, tx, 1);

        Assert.That(callOutput.Error, Is.EqualTo("nonce overflow"));
    }

    private static IEnumerable<TestCaseData> MinerPremiumNegativeCases()
    {
        yield return new TestCaseData(
            new Transaction { GasLimit = 1 },
            TransactionResult.MinerPremiumNegative,
            "miner premium is negative"
        ).SetName("Fallback");

        Address sender = TestItem.AddressA;
        UInt256 baseFee = 146_283_608_928UL;
        ulong maxFeePerGas = 140_000_000_000UL;
        Transaction descriptiveTx = new()
        {
            GasLimit = 56786,
            SenderAddress = sender,
            DecodedMaxFeePerGas = maxFeePerGas,
            Type = TxType.EIP1559
        };
        yield return new TestCaseData(
            descriptiveTx,
            TransactionResult.ErrorType.MaxFeePerGasBelowBaseFee.WithDetail($"max fee per gas less than block base fee: address {sender}, maxFeePerGas: {maxFeePerGas}, baseFee: {baseFee}"),
            $"max fee per gas less than block base fee: address {sender}, maxFeePerGas: {maxFeePerGas}, baseFee: {baseFee}"
        ).SetName("Descriptive");
    }

    [TestCaseSource(nameof(MinerPremiumNegativeCases))]
    public void Call_tx_returns_MinerPremiumIsNegativeError(Transaction tx, TransactionResult result, string expectedError)
    {
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<ITxTracer>())
            .Returns(result);

        CallOutput callOutput = _blockchainBridge.Call(Build.A.BlockHeader.TestObject, tx);

        Assert.That(callOutput.Error, Is.EqualTo(expectedError));
    }

    [Test]
    public void EstimateGas_tx_returns_MinerPremiumIsNegativeError()
    {
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.MinerPremiumNegative);

        CallOutput callOutput = _blockchainBridge.EstimateGas(Build.A.BlockHeader.TestObject, new Transaction { GasLimit = 1 }, 1);

        Assert.That(callOutput.Error, Is.EqualTo("miner premium is negative"));
    }

    [Test]
    public void EstimateGas_priced_tx_from_an_empty_sender_returns_insufficient_funds_for_transfer()
    {
        Transaction tx = new() { GasLimit = 56786, SenderAddress = TestItem.AddressA, DecodedMaxFeePerGas = 140_000_000_000UL, Type = TxType.EIP1559 };

        CallOutput callOutput = _blockchainBridge.EstimateGas(Build.A.BlockHeader.TestObject, tx, 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(callOutput.Error, Is.EqualTo(GasEstimator.InsufficientBalance), "a priced estimate needs a balance above the value, even a zero value");
            _transactionProcessor.DidNotReceive().CallAndRestore(Arg.Any<Transaction>(), Arg.Any<ITxTracer>());
        }
    }

    [Test]
    public void Call_tx_returns_BlockGasLimitExceededError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new() { GasLimit = 1 };
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.BlockGasLimitExceeded);

        CallOutput callOutput = _blockchainBridge.Call(header, tx);

        Assert.That(callOutput.Error, Is.EqualTo("Block gas limit exceeded"));
    }

    [Test]
    public void EstimateGas_tx_block_gas_limit_exceeded_returns_allowance_error()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new() { GasLimit = 1 };
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.BlockGasLimitExceeded);

        CallOutput callOutput = _blockchainBridge.EstimateGas(header, tx, 1);

        Assert.That(callOutput.Error, Is.EqualTo("gas required exceeds allowance (4000000)"));
    }


    [Test]
    public void Call_tx_returns_SenderHasDeployedCodeError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new() { GasLimit = 1 };
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.SenderHasDeployedCode);

        CallOutput callOutput = _blockchainBridge.Call(header, tx);

        Assert.That(callOutput.Error, Is.EqualTo("sender has deployed code"));
    }

    [Test]
    public void EstimateGas_tx_returns_SenderHasDeployedCodeError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new() { GasLimit = 1 };
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.SenderHasDeployedCode);

        CallOutput callOutput = _blockchainBridge.EstimateGas(header, tx, 1);

        Assert.That(callOutput.Error, Is.EqualTo("sender has deployed code"));
    }

    [Test]
    public void Call_tx_returns_TransactionSizeOverMaxInitCodeSizeError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new();
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.TransactionSizeOverMaxInitCodeSize);

        CallOutput callOutput = _blockchainBridge.Call(header, tx);

        Assert.That(callOutput.Error, Is.EqualTo("EIP-3860 - transaction size over max init code size"));
    }

    [Test]
    public void EstimateGas_tx_returns_TransactionSizeOverMaxInitCodeSizeError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new();
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.TransactionSizeOverMaxInitCodeSize);

        CallOutput callOutput = _blockchainBridge.EstimateGas(header, tx, 1);

        Assert.That(callOutput.Error, Is.EqualTo("EIP-3860 - transaction size over max init code size"));
    }

    [Test]
    public void Call_tx_returns_GasLimitBelowIntrinsicGasError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new();
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.GasLimitBelowIntrinsicGas);

        CallOutput callOutput = _blockchainBridge.Call(header, tx);

        Assert.That(callOutput.Error, Is.EqualTo("intrinsic gas too low"));
    }

    [Test]
    public void EstimateGas_tx_gas_limit_below_intrinsic_gas_returns_allowance_error()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new();
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.GasLimitBelowIntrinsicGas);

        CallOutput callOutput = _blockchainBridge.EstimateGas(header, tx, 1);

        Assert.That(callOutput.Error, Is.EqualTo("gas required exceeds allowance (4000000)"));
    }

    [Test]
    public void EstimateGas_tx_gas_limit_below_intrinsic_but_estimation_reverts_returns_revert()
    {
        BlockHeader header = Build.A.BlockHeader.TestObject;
        Transaction tx = new() { GasLimit = 1, SenderAddress = TestItem.AddressA };

        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<ITxTracer>())
            .Returns(callInfo =>
            {
                Transaction currentTx = callInfo.ArgAt<Transaction>(0);
                ITxTracer tracer = callInfo.ArgAt<ITxTracer>(1);

                if (currentTx.GasLimit < Transaction.BaseTxGasCost)
                {
                    return TransactionResult.GasLimitBelowIntrinsicGas;
                }

                tracer.ReportAction(currentTx.GasLimit, UInt256.Zero, TestItem.AddressA, TestItem.AddressB, ReadOnlyMemory<byte>.Empty, ExecutionType.TRANSACTION);
                tracer.ReportActionError(EvmExceptionType.Revert);
                tracer.MarkAsFailed(TestItem.AddressB, new GasConsumed(21000, 0), Array.Empty<byte>(), null);
                return TransactionResult.EvmException(EvmExceptionType.Revert);
            });

        CallOutput callOutput = _blockchainBridge.EstimateGas(header, tx, 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(callOutput.Error, Is.EqualTo("execution reverted"));
            Assert.That(callOutput.ExecutionReverted, Is.True);
            Assert.That(callOutput.InputError, Is.False);
            Assert.That(callOutput.OutputData, Is.EqualTo(Array.Empty<byte>()));
        }
    }

    [Test]
    public void EstimateGas_tx_gas_limit_below_intrinsic_but_estimation_succeeds_returns_estimate()
    {
        BlockHeader header = Build.A.BlockHeader.TestObject;
        Transaction tx = new() { GasLimit = 1, SenderAddress = TestItem.AddressA, To = TestItem.AddressB };

        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<ITxTracer>())
            .Returns(callInfo =>
            {
                Transaction currentTx = callInfo.ArgAt<Transaction>(0);
                ITxTracer tracer = callInfo.ArgAt<ITxTracer>(1);

                if (currentTx.GasLimit < Transaction.BaseTxGasCost)
                {
                    return TransactionResult.GasLimitBelowIntrinsicGas;
                }

                tracer.ReportAction(currentTx.GasLimit, UInt256.Zero, TestItem.AddressA, TestItem.AddressB, ReadOnlyMemory<byte>.Empty, ExecutionType.TRANSACTION);
                tracer.ReportActionEnd(currentTx.GasLimit - Transaction.BaseTxGasCost, ReadOnlyMemory<byte>.Empty);
                tracer.MarkAsSuccess(TestItem.AddressB, new GasConsumed(Transaction.BaseTxGasCost, 0), Array.Empty<byte>(), Array.Empty<LogEntry>());
                return TransactionResult.Ok;
            });

        CallOutput callOutput = _blockchainBridge.EstimateGas(header, tx, 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(callOutput.Error, Is.Null);
            Assert.That(callOutput.ExecutionReverted, Is.False);
            Assert.That(callOutput.InputError, Is.False);
            Assert.That(callOutput.GasSpent, Is.EqualTo(Transaction.BaseTxGasCost));
            Assert.That(callOutput.OutputData, Is.EqualTo(Array.Empty<byte>()));
        }
    }

    [Test]
    public void EstimateGas_tx_below_floor_gas_is_rejected_at_the_probe_gas_limit()
    {
        BlockHeader header = Build.A.BlockHeader.TestObject;
        Transaction tx = new() { GasLimit = 1_000_000, Data = new byte[1_000] };
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.GasLimitBelowFloorGas);

        CallOutput callOutput = _blockchainBridge.EstimateGas(header, tx, 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(callOutput.Error, Is.EqualTo("gas below floor data cost"), "a floor shortfall ends the estimate");
            Assert.That(callOutput.InputError, Is.True, "the transaction was rejected before execution");
            Assert.That(callOutput.GasSpent, Is.EqualTo(1_000_000ul), "the gas limit it was rejected at");
        }
    }

    [Test]
    public void Call_tx_returns_InsufficientMaxFeePerGasForSenderBalanceError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new();
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.InsufficientMaxFeePerGasForSenderBalance);

        CallOutput callOutput = _blockchainBridge.Call(header, tx);

        Assert.That(callOutput.Error, Is.EqualTo(TxErrorMessages.InsufficientFundsForGas));
    }

    [Test]
    public void Call_tx_returns_noError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new();

        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.Ok);

        CallOutput callOutput = _blockchainBridge.Call(header, tx);

        Assert.That(callOutput.Error, Is.Null);
    }

    [Test]
    public void EstimateGas_invalid_tx_returns_error()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new();

        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.Ok);

        CallOutput callOutput = _blockchainBridge.EstimateGas(header, tx, 1);

        Assert.That(callOutput.Error, Is.Not.Null);
    }

    [Test]
    public void Eth_simulate_is_lazy()
    {
        ISimulateReadOnlyBlocksProcessingEnvFactory testFactory = Substitute.For<ISimulateReadOnlyBlocksProcessingEnvFactory>();
        testFactory.Create().Returns(Substitute.For<ISimulateReadOnlyBlocksProcessingEnv>());

        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule())
            .AddSingleton<ISimulateReadOnlyBlocksProcessingEnvFactory>(testFactory)
            .Build();

        IBlockchainBridge blockchainBridge = container.Resolve<IBlockchainBridgeFactory>().CreateBlockchainBridge();
        testFactory.DidNotReceive().Create();

        try
        {
            blockchainBridge.Simulate(Build.A.EmptyBlockHeader, new SimulatePayload<TransactionWithSourceDetails>(),
                Substitute.For<ISimulateBlockTracerFactory<GethStyleTracer>>(), 10_000_000, default);
        }
        catch (Exception)
        {
        }

        testFactory.Received().Create();
    }

    [Test]
    public void Single_call_env_remembers_resolved_code_for_the_rest_of_one_call()
    {
        using IContainer container = BuildCodeLookupContainer(out ICodeInfoRepository codeSource);
        IBlockchainBridge blockchainBridge = container.Resolve<IBlockchainBridgeFactory>().CreateBlockchainBridge();

        // Any override sends a call to the single-call env; the second call reuses the pooled env of the first.
        const int calls = 2;
        for (int i = 0; i < calls; i++)
            blockchainBridge.Call(Build.A.BlockHeader.TestObject, new Transaction(), blobBaseFeeOverride: UInt256.One);

        AssertCodeSourceLookups(codeSource, calls);
    }

    [Test]
    public void Single_call_env_forgets_resolved_code_when_a_call_is_cancelled()
    {
        CancelFirstCall cancelFirstCall = new() { Pending = true };
        using IContainer container = BuildCodeLookupContainer(out ICodeInfoRepository codeSource, cancelFirstCall);
        IBlockchainBridge blockchainBridge = container.Resolve<IBlockchainBridgeFactory>().CreateBlockchainBridge();

        // The first call resolves the code and is cancelled mid-execution; the second rents the same pooled env.
        Assert.Throws<OperationCanceledException>(() => blockchainBridge.Call(Build.A.BlockHeader.TestObject, new Transaction(), blobBaseFeeOverride: UInt256.One));
        blockchainBridge.Call(Build.A.BlockHeader.TestObject, new Transaction(), blobBaseFeeOverride: UInt256.One);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cancelFirstCall.Pending, Is.False);
            AssertCodeSourceLookups(codeSource, 2);
        }
    }

    [Test]
    public void Envs_from_the_overridable_env_factory_do_not_memoize_code()
    {
        using IContainer container = BuildCodeLookupContainer(out ICodeInfoRepository codeSource);
        // Tracing and eth_simulateV1 build on these envs and run several transactions in one scope.
        IOverridableEnv env = container.Resolve<IOverridableEnvFactory>().Create();
        using ILifetimeScope envLifetime = container.BeginLifetimeScope(builder => builder.AddModule(env));

        using (Scope<ITransactionProcessor> scope = envLifetime.Resolve<IOverridableEnv<ITransactionProcessor>>().BuildAndOverride(Build.A.BlockHeader.TestObject))
        {
            scope.Component.CallAndRestore(new Transaction(), NullTxTracer.Instance);
        }

        AssertCodeSourceLookups(codeSource, CodeLookupsPerCall);
    }

    [TestCase(ExecutionOptions.CommitAndRestore, true)]
    [TestCase(ExecutionOptions.Commit, false)]
    [TestCase(ExecutionOptions.SkipValidationAndCommit, false)]
    [TestCase(ExecutionOptions.BuildUp, false)]
    public void Single_call_env_keeps_resolved_code_only_across_restored_transactions(ExecutionOptions first, bool kept)
    {
        using IContainer container = BuildCodeLookupContainer(out ICodeInfoRepository codeSource);
        // The bridge restores every transaction it runs; this stands in for a consumer that runs several in one scope.
        BlockchainBridgeFactory factory = (BlockchainBridgeFactory)container.Resolve<IBlockchainBridgeFactory>();
        IOverridableEnv<BlockchainBridge.BlockProcessingComponents> env = factory.BuildSingleEnv();
        using IDisposable envLifetime = (IDisposable)env;

        using (Scope<BlockchainBridge.BlockProcessingComponents> scope = env.BuildAndOverride(Build.A.BlockHeader.TestObject))
        {
            ITransactionProcessor transactionProcessor = scope.Component.TransactionProcessor;
            transactionProcessor.Process(new Transaction(), NullTxTracer.Instance, first);
            transactionProcessor.CallAndRestore(new Transaction(), NullTxTracer.Instance);
        }

        AssertCodeSourceLookups(codeSource, kept ? 1 : 2);
    }

    private const int CodeLookupsPerCall = 3;

    private static IContainer BuildCodeLookupContainer(out ICodeInfoRepository codeSource, CancelFirstCall? cancelFirstCall = null)
    {
        codeSource = Substitute.For<ICodeInfoRepository>();
        codeSource.GetCachedCodeInfo(Arg.Any<Address>(), Arg.Any<bool>(), Arg.Any<IReleaseSpec>(), out Arg.Any<Address?>())
            .Returns(new CodeInfo(new byte[] { 0x60, 0x00 }));
        return new ContainerBuilder()
            .AddModule(new TestNethermindModule())
            .AddScoped(codeSource)
            .AddSingleton(cancelFirstCall ?? new CancelFirstCall())
            .AddScoped<ITransactionProcessor, CodeLookupTransactionProcessor>()
            .Build();
    }

    private static void AssertCodeSourceLookups(ICodeInfoRepository codeSource, int expected) =>
        codeSource.Received(expected).GetCachedCodeInfo(TestItem.AddressC, Arg.Any<bool>(), Arg.Any<IReleaseSpec>(), out Arg.Any<Address?>());

    /// <summary>While <see cref="Pending"/> is set, the next transaction is cancelled after its code lookups, as a cancelled request's tracer would, and the flag clears.</summary>
    private sealed class CancelFirstCall
    {
        public bool Pending { get; set; }
    }

    // Stands in for the EVM: every call resolves the same contract's code a few times through the env's repository.
    private sealed class CodeLookupTransactionProcessor(ICodeInfoRepository codeInfoRepository, CancelFirstCall cancelFirstCall) : ITransactionProcessor
    {
        public TransactionResult Process(Transaction transaction, ITxTracer txTracer, ExecutionOptions options)
        {
            for (int i = 0; i < CodeLookupsPerCall; i++) codeInfoRepository.GetCachedCodeInfo(TestItem.AddressC, Prague.Instance);
            if (cancelFirstCall.Pending)
            {
                cancelFirstCall.Pending = false;
                throw new OperationCanceledException();
            }

            return TransactionResult.Ok;
        }

        public void SetBlockExecutionContext(BlockHeader blockHeader) { }

        public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext) { }
    }

    [Test]
    public void HasStateForBlock_returns_true_when_stateReader_returns_true()
    {
        BlockHeader header = Build.A.BlockHeader.WithNumber(100).WithStateRoot(TestItem.KeccakA).TestObject;

        _stateReader.HasStateForBlock(header).Returns(true);

        Assert.That(_blockchainBridge.HasStateForBlock(header), Is.True);
    }

    [Test]
    public void HasStateForBlock_returns_false_when_stateReader_returns_false()
    {
        BlockHeader header = Build.A.BlockHeader.WithNumber(250).WithStateRoot(TestItem.KeccakA).TestObject;

        _stateReader.HasStateForBlock(header).Returns(false);

        Assert.That(_blockchainBridge.HasStateForBlock(header), Is.False);
    }

    // The request cap depletes by the receipt gas used, while the two block budgets each track one EIP-8037 dimension.
    [TestCase(50_000ul, 30_000ul, 10_000ul, 190_000ul, 30_000ul, 30_000ul, TestName = "Simulate adapter depletes the request cap by the receipt gas used")]
    [TestCase(80_000ul, 60_000ul, 10_000ul, 190_000ul, 0ul, 0ul, TestName = "Simulate adapter accepts exact block and state budgets")]
    [TestCase(80_001ul, 60_001ul, 10_000ul, 190_000ul, 0ul, 0ul, TestName = "Simulate adapter saturates block budgets exceeded by one")]
    [TestCase(ulong.MaxValue - 1, 60_000ul, ulong.MaxValue - 1, 0ul, 0ul, 0ul, TestName = "Simulate adapter saturates the request cap and block budgets instead of wrapping")]
    public void Simulate_adapter_uses_block_gas_used_for_budgets(
        ulong blockGasUsed,
        ulong blockStateGasUsed,
        ulong spentGas,
        ulong expectedTotalGasLeft,
        ulong expectedBlockGasLeft,
        ulong expectedBlockStateGasLeft)
    {
        SimulateRequestState simulateRequestState = new()
        {
            TotalGasLeft = 200_000,
            BlockGasLeft = 80_000,
            BlockStateGasLeft = 60_000,
            Validate = true,
        };
        simulateRequestState.Calls =
            [
                new()
                {
                    HadGasLimitInRequest = true,
                    Transaction = Build.A.Transaction.WithSenderAddress(TestItem.AddressA).TestObject
                }
            ];

        ITransactionProcessor processor = Substitute.For<ITransactionProcessor>();
        processor.Execute(Arg.Any<Transaction>(), Arg.Any<ITxTracer>())
            .Returns(ci =>
            {
                Transaction tx = ci.Arg<Transaction>();
                tx.SpentGas = spentGas;
                tx.BlockGasUsed = blockGasUsed;
                GasConsumed gasConsumed = new(spentGas, blockGasUsed, blockGasUsed, blockStateGasUsed);
                ci.Arg<ITxTracer>().MarkAsSuccess(Address.Zero, gasConsumed, [], []);
                return TransactionResult.Ok;
            });

        SimulateTransactionProcessorAdapter adapter = new(processor, simulateRequestState);
        Transaction transaction = Build.A.Transaction.WithSenderAddress(TestItem.AddressA).WithNonce(1)
            .WithGasLimit(60_000).SignedAndResolved(TestItem.PrivateKeyA).TestObject;

        using BlockReceiptsTracer receiptsTracer = CreateReceiptsTracer(transaction);
        ExecuteWithReceiptsTracer(adapter, receiptsTracer, transaction);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(simulateRequestState.TotalGasLeft, Is.EqualTo(expectedTotalGasLeft));
            Assert.That(simulateRequestState.BlockGasLeft, Is.EqualTo(expectedBlockGasLeft));
            Assert.That(simulateRequestState.BlockStateGasLeft, Is.EqualTo(expectedBlockStateGasLeft));
        }
    }

    [Test]
    public void Simulate_adapter_clamps_omitted_gas_to_remaining_state_budget()
    {
        const ulong blockStateGasLimit = 300_000;
        const ulong stateGasPerWrite = (ulong)GasCostOf.SSetState;
        SimulateRequestState simulateRequestState = new()
        {
            TotalGasLeft = 1_000_000,
            BlockGasLeft = 300_000,
            BlockStateGasLeft = blockStateGasLimit,
            Validate = false,
        };
        simulateRequestState.Calls =
            [
                new() { HadGasLimitInRequest = true, Transaction = new Transaction() },
                new() { HadGasLimitInRequest = true, Transaction = new Transaction() },
                new() { HadGasLimitInRequest = false, Transaction = new Transaction() }
            ];

        List<ulong> executedGasLimits = [];
        ITransactionProcessor processor = Substitute.For<ITransactionProcessor>();
        processor.Trace(Arg.Any<Transaction>(), Arg.Any<ITxTracer>())
            .Returns(ci =>
            {
                Transaction tx = ci.Arg<Transaction>();
                executedGasLimits.Add(tx.GasLimit);
                tx.SpentGas = 10_000;
                tx.BlockGasUsed = 10_000;
                ulong stateGasUsed = executedGasLimits.Count <= 2 ? stateGasPerWrite : 0;
                GasConsumed gasConsumed = new(10_000, 10_000, 10_000, stateGasUsed);
                ci.Arg<ITxTracer>().MarkAsSuccess(Address.Zero, gasConsumed, [], []);
                return TransactionResult.Ok;
            });

        SimulateTransactionProcessorAdapter adapter = new(processor, simulateRequestState);
        Transaction first = new() { GasLimit = 150_000 };
        Transaction second = new() { GasLimit = 150_000 };
        Transaction third = new() { GasLimit = 500_000 };
        using BlockReceiptsTracer receiptsTracer = CreateReceiptsTracer(first, second, third);
        ExecuteWithReceiptsTracer(adapter, receiptsTracer, first);
        ExecuteWithReceiptsTracer(adapter, receiptsTracer, second);
        ExecuteWithReceiptsTracer(adapter, receiptsTracer, third);

        // Each write must deplete the state budget by its own state gas, not by the tracer's running total.
        Assert.That(executedGasLimits, Is.EqualTo(new ulong[] { 150_000, 150_000, blockStateGasLimit - 2 * stateGasPerWrite }));
    }

    [Test]
    public void Simulate_adapter_saturates_execution_budget_before_clamping_following_omitted_gas()
    {
        SimulateRequestState simulateRequestState = new()
        {
            TotalGasLeft = 300_000,
            BlockGasLeft = 80_000,
            BlockStateGasLeft = 300_000,
            Validate = false,
        };
        simulateRequestState.Calls =
            [
                new() { HadGasLimitInRequest = true, Transaction = new Transaction() },
                new() { HadGasLimitInRequest = false, Transaction = new Transaction() }
            ];

        List<ulong> executedGasLimits = [];
        ITransactionProcessor processor = Substitute.For<ITransactionProcessor>();
        processor.Trace(Arg.Any<Transaction>(), Arg.Any<ITxTracer>())
            .Returns(ci =>
            {
                Transaction tx = ci.Arg<Transaction>();
                executedGasLimits.Add(tx.GasLimit);
                ulong blockGasUsed = executedGasLimits.Count == 1 ? 100_000UL : 0;
                tx.SpentGas = blockGasUsed;
                tx.BlockGasUsed = blockGasUsed;
                GasConsumed gasConsumed = new(blockGasUsed, blockGasUsed, blockGasUsed);
                ci.Arg<ITxTracer>().MarkAsSuccess(Address.Zero, gasConsumed, [], []);
                return TransactionResult.Ok;
            });

        SimulateTransactionProcessorAdapter adapter = new(processor, simulateRequestState);
        Transaction first = new() { GasLimit = 80_000 };
        Transaction second = new() { GasLimit = 500_000 };
        using BlockReceiptsTracer receiptsTracer = CreateReceiptsTracer(first, second);
        ExecuteWithReceiptsTracer(adapter, receiptsTracer, first);
        ExecuteWithReceiptsTracer(adapter, receiptsTracer, second);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(executedGasLimits, Is.EqualTo(new ulong[] { 80_000, 0 }));
            Assert.That(simulateRequestState.TotalGasLeft, Is.EqualTo(200_000));
            Assert.That(simulateRequestState.BlockGasLeft, Is.Zero);
        }
    }

    private static BlockReceiptsTracer CreateReceiptsTracer(params Transaction[] transactions)
    {
        BlockReceiptsTracer receiptsTracer = new();
        receiptsTracer.StartNewBlockTrace(Build.A.Block.WithTransactions(transactions).TestObject);
        return receiptsTracer;
    }

    private static void ExecuteWithReceiptsTracer(
        SimulateTransactionProcessorAdapter adapter,
        BlockReceiptsTracer receiptsTracer,
        Transaction transaction)
    {
        receiptsTracer.StartNewTxTrace(transaction);
        adapter.Execute(transaction, receiptsTracer);
        receiptsTracer.EndTxTrace();
    }

    [Test]
    public void Simulate_adapter_rejects_explicit_gas_above_remaining_block_gas([Values] bool validate)
    {
        SimulateRequestState simulateRequestState = new()
        {
            TotalGasLeft = 300_000,
            BlockGasLeft = 80_000,
            Validate = validate,
        };
        simulateRequestState.Calls = [new() { HadGasLimitInRequest = true, GasLimitToFitBlock = 80_001, Transaction = new Transaction() }];

        ITransactionProcessor processor = Substitute.For<ITransactionProcessor>();
        SimulateTransactionProcessorAdapter adapter = new(processor, simulateRequestState);
        Transaction explicitGas = new() { GasLimit = 80_001 };

        InvalidTransactionException exception = Assert.Throws<InvalidTransactionException>(
            () => adapter.PrepareForInclusionCheck(explicitGas, ulong.MaxValue))!;
        Assert.That(exception.Reason.Error, Is.EqualTo(TransactionResult.ErrorType.BlockGasLimitExceeded));
    }

    // With validation, EIP-8037 reserves at most TX_MAX_GAS_LIMIT of execution gas, so a full cap left in the execution
    // budget no longer binds the default; without validation the whole gas limit is execution gas.
    [TestCase(20_000_000ul, true, 40_000_000ul, TestName = "Simulate adapter defaults strict gas to the state budget past a full execution cap")]
    [TestCase(20_000_000ul, false, 20_000_000ul, TestName = "Simulate adapter defaults non-strict gas to the smaller budget")]
    [TestCase(10_000_000ul, true, 10_000_000ul, TestName = "Simulate adapter defaults strict gas to the smaller budget below a full execution cap")]
    public void Simulate_adapter_defaults_omitted_gas_to_block_gas_available(ulong blockGasLeft, bool validate, ulong expectedGasLimit)
    {
        SimulateRequestState simulateRequestState = new()
        {
            TotalGasLeft = 100_000_000,
            BlockGasLeft = blockGasLeft,
            Validate = validate,
        };
        simulateRequestState.Calls = [new() { HadGasLimitInRequest = false, Transaction = new Transaction() }];

        SimulateTransactionProcessorAdapter adapter = new(Substitute.For<ITransactionProcessor>(), simulateRequestState);
        adapter.SetBlockExecutionContext(new BlockExecutionContext(Build.A.BlockHeader.TestObject, Amsterdam.Instance, UInt256.Zero));
        Transaction omittedGas = new();
        adapter.PrepareForInclusionCheck(omittedGas, 40_000_000);

        Assert.That(omittedGas.GasLimit, Is.EqualTo(expectedGasLimit));
    }

    // Two empty unsigned frames reserve about 612,950 execution and 600,000 state gas: both fit a 1,000,000-gas
    // block although their summed gas limit (1,200,000) does not.
    [TestCase(300_000ul, false, TestName = "Simulate adapter admits a frame transaction whose reservations fit the block")]
    [TestCase(500_000ul, true, TestName = "Simulate adapter rejects a frame transaction whose execution reservation exceeds the block")]
    public void Simulate_adapter_checks_frame_transactions_by_their_block_gas_reservations(ulong frameExecutionGas, bool expectRejected)
    {
        SimulateRequestState simulateRequestState = new()
        {
            TotalGasLeft = 10_000_000,
            BlockGasLeft = 1_000_000,
            Validate = true,
        };
        simulateRequestState.Calls = [new() { HadGasLimitInRequest = true, GasLimitToFitBlock = 2 * (frameExecutionGas + 300_000), Transaction = new Transaction() }];

        SimulateTransactionProcessorAdapter adapter = new(Substitute.For<ITransactionProcessor>(), simulateRequestState);
        adapter.SetBlockExecutionContext(new BlockExecutionContext(Build.A.BlockHeader.TestObject, Eip8141Prototype.Instance, UInt256.Zero));
        Transaction frameTx = Build.A.Transaction.WithType(TxType.FrameTx).WithSenderAddress(TestItem.AddressA).TestObject;
        frameTx.Frames =
        [
            new TxFrame(FrameMode.Default, FrameFlags.None, TestItem.AddressB, frameExecutionGas, 300_000, UInt256.Zero, default),
            new TxFrame(FrameMode.Default, FrameFlags.None, TestItem.AddressB, frameExecutionGas, 300_000, UInt256.Zero, default)
        ];
        frameTx.GasLimit = 2 * (frameExecutionGas + 300_000);

        if (expectRejected)
        {
            TransactionResult reason = Assert.Throws<InvalidTransactionException>(() => adapter.PrepareForInclusionCheck(frameTx, 1_000_000))!.Reason;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(reason.Error, Is.EqualTo(TransactionResult.ErrorType.BlockGasLimitExceeded));
                Assert.That(reason.ErrorDescription, Does.EndWith("remaining: 1000000, required: 1012950"));
            }
        }
        else
        {
            Assert.DoesNotThrow(() => adapter.PrepareForInclusionCheck(frameTx, 1_000_000));
        }
    }

    [Test]
    public void Simulate_adapter_leaves_a_frame_transaction_without_computable_reservations_to_the_processor()
    {
        SimulateRequestState simulateRequestState = new()
        {
            TotalGasLeft = 10_000_000,
            BlockGasLeft = 1_000_000,
            Validate = true,
        };
        simulateRequestState.Calls = [new() { HadGasLimitInRequest = true, GasLimitToFitBlock = 1_000_000, Transaction = new Transaction() }];

        SimulateTransactionProcessorAdapter adapter = new(Substitute.For<ITransactionProcessor>(), simulateRequestState);
        adapter.SetBlockExecutionContext(new BlockExecutionContext(Build.A.BlockHeader.TestObject, Eip8141Prototype.Instance, UInt256.Zero));
        Transaction frameTx = Build.A.Transaction.WithType(TxType.FrameTx).WithSenderAddress(TestItem.AddressA).WithGasLimit(1_000_000).TestObject;
        frameTx.Frames =
        [
            new TxFrame(FrameMode.Default, FrameFlags.None, TestItem.AddressB, ulong.MaxValue, 0, UInt256.Zero, default),
            new TxFrame(FrameMode.Default, FrameFlags.None, TestItem.AddressB, 1, 0, UInt256.Zero, default)
        ];

        Assert.DoesNotThrow(() => adapter.PrepareForInclusionCheck(frameTx, 1_000_000));
    }

    private static IEnumerable<TestCaseData> OverrideRoutingCases()
    {
        foreach (OverrideCall call in Enum.GetValues<OverrideCall>())
        {
            foreach (OverrideInput input in Enum.GetValues<OverrideInput>())
            {
                if (call != OverrideCall.EstimateFrameGas || input != OverrideInput.BlobFeeArgument)
                    yield return new TestCaseData(call, input);
            }
        }
    }

    public enum OverrideCall { Call, EstimateGas, EstimateFrameGas }
    public enum OverrideInput { None, Empty, Number, PrevRandao, Time, GasLimit, FeeRecipient, BaseFeePerGas, BlobBaseFee, BeaconRoot, Withdrawals, State, BlobFeeArgument }
}
