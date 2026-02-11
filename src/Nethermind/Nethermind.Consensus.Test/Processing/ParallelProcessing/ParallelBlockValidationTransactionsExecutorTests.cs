// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Processing.ParallelProcessing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Utils;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;
using Metrics = Nethermind.Consensus.Processing.ParallelProcessing.Metrics;

namespace Nethermind.Consensus.Test.Processing.ParallelProcessing;

[NonParallelizable] // don't parallelize, each test already uses parallelization to process transactions
public class ParallelBlockValidationTransactionsExecutorTests
{
    private static readonly EthereumEcdsa Ecdsa = new(BlockchainIds.Mainnet);
    private static readonly Address AddressG = TestItem.Addresses[6];
    private static readonly Address AddressH = TestItem.Addresses[7];

    private class ParallelTestBlockchain(IBlocksConfig blocksConfig, IReleaseSpec releaseSpec) : TestBlockchain
    {
        public TransactionDelayPolicy DelayPolicy { get; } = new();

        public static async Task<ParallelTestBlockchain> Create(
            IBlocksConfig blocksConfig,
            IReleaseSpec releaseSpec = null,
            Action<ContainerBuilder> configurer = null,
            long? testTimeoutMs = null)
        {
            ParallelTestBlockchain chain = new(blocksConfig, releaseSpec ?? Osaka.Instance)
            {
                TestTimeout = testTimeoutMs ?? DefaultTimeout
            };
            await chain.Build(configurer);
            return chain;
        }

        protected override Task AddBlocksOnStart() => Task.CompletedTask;

        protected override IEnumerable<IConfig> CreateConfigs() => [blocksConfig];

        protected override AutoCancelTokenSource CreateCancellationSource() =>
            AutoCancelTokenSource.ThatCancelAfter(
                Debugger.IsAttached
                    ? TimeSpan.FromMilliseconds(-1)
                    : TimeSpan.FromMilliseconds(TestTimeout));

        protected override ContainerBuilder ConfigureContainer(ContainerBuilder builder, IConfigProvider configProvider) =>
            base.ConfigureContainer(builder, configProvider)
                .AddSingleton<ISpecProvider>(new TestSpecProvider(releaseSpec))
                .AddSingleton<IGenesisPostProcessor>(new GenesisGasLimitOverride(TestBlockGasLimit))
                .AddSingleton(DelayPolicy)
                .AddDecorator<ITransactionProcessor, DelayedTransactionProcessor>();
    }

    private sealed class GenesisGasLimitOverride(long gasLimit) : IGenesisPostProcessor
    {
        public void PostProcess(Block genesis) => genesis.Header.GasLimit = gasLimit;
    }

    /// <summary>
    /// Helper that wraps parallel and single-threaded blockchains for comparison testing.
    /// Executes operations on both chains and provides assertion helpers.
    /// </summary>
    private sealed class DualBlockchain(ParallelTestBlockchain parallel, ParallelTestBlockchain single)
        : IAsyncDisposable
    {
        private ParallelTestBlockchain Parallel { get; } = parallel;
        private ParallelTestBlockchain Single { get; } = single;

        public static async Task<DualBlockchain> Create(IReleaseSpec releaseSpec = null, long? testTimeoutMs = null) =>
            new(await ParallelTestBlockchain.Create(BuildConfig(true), releaseSpec, testTimeoutMs: testTimeoutMs),
                await ParallelTestBlockchain.Create(BuildConfig(false), releaseSpec, testTimeoutMs: testTimeoutMs));

        public async Task<BlockPair> AddBlock(params Transaction[] transactions) =>
            new(await AddBlockAndWaitForHead(Parallel, transactions), await AddBlockAndWaitForHead(Single, transactions));

        public async Task<BlockPair> AddBlockWithDelays(TxDelay[] delays, params Transaction[] transactions) =>
            new(await AddBlockAndWaitForHead(Parallel, transactions, delays),
                await AddBlockAndWaitForHead(Single, transactions));

        public BlockPair ProcessBlockDirect(BlockPair parentBlocks, params Transaction[] transactions) =>
            new(ProcessBlockDirectWithScope(Parallel, parentBlocks.Parallel.Header, transactions),
                ProcessBlockDirectWithScope(Single, parentBlocks.Single.Header, transactions));

        public BlockPair ProcessBlockDirectOnHead(TxDelay[] delays, params Transaction[] transactions)
        {
            Parallel.DelayPolicy.SetDelays(delays);
            return new(ProcessBlockDirectWithScope(Parallel, Parallel.BlockTree.Head!.Header, transactions),
                ProcessBlockDirectWithScope(Single, Single.BlockTree.Head!.Header, transactions));
        }

        public (BlockPair Blocks, ReceiptPair Receipts) ProcessBlockDirectWithReceiptsOnHead(
            TxDelay[] delays,
            params Transaction[] transactions)
        {
            return ProcessBlockOnHead(
                delays,
                parent => Build.A.Block
                    .WithTransactions(transactions)
                    .WithParent(parent)
                    .WithBaseFeePerGas(1.GWei())
                    .TestObject,
                (chain, header) => chain.SpecProvider.GetSpec(header));
        }

        public BlockPair ProcessBlockWithFeeRecipients(Address beneficiary, Address feeCollector, TxDelay[] delays, params Transaction[] transactions)
        {
            (BlockPair blocks, _) = ProcessBlockOnHead(
                delays,
                parent => Build.A.Block
                    .WithTransactions(transactions)
                    .WithParent(parent)
                    .WithBeneficiary(beneficiary)
                    .WithBaseFeePerGas(1.GWei())
                    .TestObject,
                (chain, header) =>
                {
                    OverridableReleaseSpec spec = (OverridableReleaseSpec)chain.SpecProvider.GetSpec(header);
                    spec.FeeCollector = feeCollector;
                    return spec;
                });
            return blocks;
        }

        public ValueTask DisposeAsync()
        {
            Parallel.Dispose();
            Single.Dispose();
            return ValueTask.CompletedTask;
        }

        private static Block ProcessBlockDirectWithScope(ParallelTestBlockchain chain, BlockHeader parent, Transaction[] transactions)
        {
            using IDisposable scope = chain.MainProcessingContext.WorldState.BeginScope(parent);
            return ParallelBlockValidationTransactionsExecutorTests.ProcessBlockDirect(chain, parent, transactions, parent.GasLimit);
        }

        private (BlockPair Blocks, ReceiptPair Receipts) ProcessBlockOnHead(
            TxDelay[] delays,
            Func<BlockHeader, Block> buildBlock,
            Func<ParallelTestBlockchain, BlockHeader, IReleaseSpec> buildSpec)
        {
            Parallel.DelayPolicy.SetDelays(delays);
            (Block parallelBlock, TxReceipt[] parallelReceipts) = ProcessBlockOnChain(Parallel, buildBlock, buildSpec);
            (Block singleBlock, TxReceipt[] singleReceipts) = ProcessBlockOnChain(Single, buildBlock, buildSpec);
            return (new BlockPair(parallelBlock, singleBlock), new ReceiptPair(parallelReceipts, singleReceipts));
        }

        private static (Block Block, TxReceipt[] Receipts) ProcessBlockOnChain(
            ParallelTestBlockchain chain,
            Func<BlockHeader, Block> buildBlock,
            Func<ParallelTestBlockchain, BlockHeader, IReleaseSpec> buildSpec)
        {
            BlockHeader head = chain.BlockTree.Head!.Header;
            Block block = buildBlock(head);
            IReleaseSpec spec = buildSpec(chain, block.Header);
            using IDisposable scope = chain.MainProcessingContext.WorldState.BeginScope(head);
            (_, TxReceipt[] receipts) = chain.MainProcessingContext.BlockProcessor.ProcessOne(
                block, ProcessingOptions.NoValidation, NullBlockTracer.Instance, spec);
            return (block, receipts);
        }

        private static async Task<Block> AddBlockAndWaitForHead(ParallelTestBlockchain chain, Transaction[] transactions) =>
            await chain.AddBlock(transactions);

        private static async Task<Block> AddBlockAndWaitForHead(ParallelTestBlockchain chain, Transaction[] transactions, TxDelay[] delays)
        {
            chain.DelayPolicy.SetDelays(delays);
            return await chain.AddBlock(transactions);
        }
    }

    private readonly record struct BlockPair(Block Parallel, Block Single)
    {
        public void AssertStateRootsMatch() =>
            Assert.That(Parallel.Header.StateRoot, Is.EqualTo(Single.Header.StateRoot));

        public void AssertFullMatch(int expectedTxCount)
        {
            Block p = Parallel, s = Single;
            Assert.Multiple(() =>
            {
                Assert.That(p.Transactions, Has.Length.EqualTo(expectedTxCount));
                Assert.That(s.Transactions, Has.Length.EqualTo(expectedTxCount));
                Assert.That(p.Header.GasUsed, Is.EqualTo(s.Header.GasUsed));
                Assert.That(p.Header.StateRoot, Is.EqualTo(s.Header.StateRoot));
            });
        }
    }

    private readonly record struct ReceiptPair(TxReceipt[] Parallel, TxReceipt[] Single)
    {
        public void AssertSuccessful(int expectedCount)
        {
            TxReceipt[] parallel = Parallel;
            TxReceipt[] single = Single;
            Assert.Multiple(() =>
            {
                Assert.That(parallel, Has.Length.EqualTo(expectedCount), "Parallel should process all transactions");
                Assert.That(single, Has.Length.EqualTo(expectedCount), "Single should process all transactions");
                for (int i = 0; i < expectedCount; i++)
                {
                    Assert.That(parallel[i].StatusCode, Is.EqualTo(1), $"Parallel tx{i} should succeed");
                    Assert.That(single[i].StatusCode, Is.EqualTo(1), $"Single tx{i} should succeed");
                }
            });
        }
    }

    public readonly record struct ExpectedMetrics(
        int TxCount,
        long Reexecutions,
        long Revalidations,
        long BlockedReads,
        long ParallelizationPercent)
    {
        public static ExpectedMetrics Independent(int txCount) => Create(txCount, reexecutions: 0);

        public static ExpectedMetrics AllDependent(int txCount) => Create(txCount, reexecutions: 0);

        public static ExpectedMetrics Create(int txCount, long reexecutions, long blockedReads = 0, long? revalidations = null)
        {
            long resolvedRevalidations = revalidations ?? reexecutions;
            long parallelizationPercent = Metrics.CalculateParallelizationPercent(txCount, reexecutions);
            return new ExpectedMetrics(txCount, reexecutions, resolvedRevalidations, blockedReads, parallelizationPercent);
        }

        public void AssertAgainst(ParallelBlockMetrics snapshot)
        {
            ExpectedMetrics expected = this;
            Assert.Multiple(() =>
            {
                Assert.That(snapshot.TxCount, Is.EqualTo(expected.TxCount), "TxCount");
                Assert.That(snapshot.Reexecutions, Is.EqualTo(expected.Reexecutions), "Reexecutions");
                Assert.That(snapshot.Revalidations, Is.EqualTo(expected.Revalidations), "Revalidations");
                Assert.That(snapshot.BlockedReads, Is.EqualTo(expected.BlockedReads), "BlockedReads");
                Assert.That(snapshot.ParallelizationPercent, Is.EqualTo(expected.ParallelizationPercent), "ParallelizationPercent");
            });
        }
    }

    // Raise the gas limit so large test blocks fit without tx pool timeouts.
    private const long TestBlockGasLimit = 30_000_000;
    private const int ShortDelayMs = 50;
    private const int LongDelayMs = 200;
    private static readonly TxDelay[] NoDelays = [];

    public readonly record struct TxDelay(Transaction Transaction, int Milliseconds);

    private static TxDelay Delay(Transaction transaction, int milliseconds) => new(transaction, milliseconds);

    private sealed class TransactionDelayPolicy
    {
        private readonly Dictionary<Hash256, int> _delays = new();

        public void SetDelays(TxDelay[] delays)
        {
            _delays.Clear();
            foreach (TxDelay delay in delays)
            {
                if (delay.Milliseconds > 0)
                {
                    Hash256 hash = delay.Transaction.Hash;
                    if (hash is not null)
                    {
                        _delays[hash] = delay.Milliseconds;
                    }
                }
            }
        }

        public bool TryGetDelay(Transaction transaction, out int milliseconds)
        {
            milliseconds = 0;
            return transaction.Hash is not null && _delays.TryGetValue(transaction.Hash, out milliseconds);
        }
    }

    private sealed class DelayedTransactionProcessor(ITransactionProcessor inner, TransactionDelayPolicy delayPolicy) : ITransactionProcessor
    {
        public TransactionResult Execute(Transaction transaction, ITxTracer txTracer)
        {
            ApplyDelay(transaction);
            return inner.Execute(transaction, txTracer);
        }

        public TransactionResult CallAndRestore(Transaction transaction, ITxTracer txTracer)
        {
            ApplyDelay(transaction);
            return inner.CallAndRestore(transaction, txTracer);
        }

        public TransactionResult BuildUp(Transaction transaction, ITxTracer txTracer)
        {
            ApplyDelay(transaction);
            return inner.BuildUp(transaction, txTracer);
        }

        public TransactionResult Trace(Transaction transaction, ITxTracer txTracer)
        {
            ApplyDelay(transaction);
            return inner.Trace(transaction, txTracer);
        }

        public TransactionResult Warmup(Transaction transaction, ITxTracer txTracer)
        {
            ApplyDelay(transaction);
            return inner.Warmup(transaction, txTracer);
        }

        public void SetBlockExecutionContext(BlockHeader blockHeader) => inner.SetBlockExecutionContext(blockHeader);

        public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext) => inner.SetBlockExecutionContext(in blockExecutionContext);

        private void ApplyDelay(Transaction transaction)
        {
            if (delayPolicy.TryGetDelay(transaction, out int milliseconds) && milliseconds > 0)
            {
                Thread.Sleep(milliseconds);
            }
        }
    }

    private static ParallelBlockMetrics GetLastBlockMetrics()
    {
        bool result = Metrics.TryGetLastBlockSnapshot(out ParallelBlockMetrics snapshot);
        Assert.That(result, Is.True, "Expected parallel block metrics snapshot.");
        return snapshot;
    }

    private static void AssertLastBlockMetrics(ExpectedMetrics expected) =>
        expected.AssertAgainst(GetLastBlockMetrics());

    private static IBlocksConfig BuildConfig(bool parallel) =>
        new BlocksConfig
        {
            MinGasPrice = 0,
            PreWarmStateOnBlockProcessing = !parallel,
            ParallelBlockProcessing = parallel
        };

    public static IEnumerable<TestCaseData> SimpleBlocksTests
    {
        get
        {
            yield return Test("1 Transaction", [Tx(TestItem.PrivateKeyA, TestItem.AddressB, 0)],
                ExpectedMetrics.Independent(1));

            yield return Test("3 Transactions, nonce dependency",
            [
                Tx(TestItem.PrivateKeyA, TestItem.AddressB, 0),
                Tx(TestItem.PrivateKeyA, TestItem.AddressC, 1),
                Tx(TestItem.PrivateKeyA, TestItem.AddressB, 2)
            ],
            // Nonce chain is pre-ordered; no runtime reexecs or blocked reads.
            ExpectedMetrics.AllDependent(3));

            yield return Test("5 Transactions, nonce dependency",
            [
                Tx(TestItem.PrivateKeyA, TestItem.AddressB, 0, 1.Ether()),
                Tx(TestItem.PrivateKeyA, TestItem.AddressC, 1, 2.Ether()),
                Tx(TestItem.PrivateKeyA, TestItem.AddressD, 2, 3.Ether()),
                Tx(TestItem.PrivateKeyA, TestItem.AddressE, 3, 4.Ether()),
                Tx(TestItem.PrivateKeyA, TestItem.AddressF, 4, 5.Ether()),
            ],
            // Nonce chain is pre-ordered; no runtime reexecs or blocked reads.
            ExpectedMetrics.AllDependent(5));

            Transaction balanceTx0 = Tx(TestItem.PrivateKeyA, TestItem.AddressB, 0, 10.Ether());
            Transaction balanceTx1 = Tx(TestItem.PrivateKeyA, TestItem.AddressC, 1, 5.Ether());
            Transaction balanceTx2 = Tx(TestItem.PrivateKeyB, TestItem.AddressD, 0, 3.Ether());
            yield return Test("Balance changes across transactions",
            [
                balanceTx0,
                balanceTx1,
                balanceTx2,
            ],
            // Nonce chain is pre-ordered; delayed funding yields blocked reads and reexecs.
            ExpectedMetrics.Create(3, reexecutions: 2, blockedReads: 2, revalidations: 0),
            delays: [Delay(balanceTx0, ShortDelayMs)]);

            Transaction sharedTx0 = Tx(TestItem.PrivateKeyA, TestItem.AddressD, 0, 500.Ether());
            Transaction sharedTx1 = Tx(TestItem.PrivateKeyB, TestItem.AddressD, 0, 500.Ether());
            Transaction sharedTx2 = Tx(TestItem.PrivateKeyC, TestItem.AddressD, 0, 500.Ether());
            yield return Test("Balance transfers from multiple senders",
            [
                sharedTx0,
                sharedTx1,
                sharedTx2,
            ],
            // tx2 is delayed so it reads tx1's write before the delayed base write lands.
            ExpectedMetrics.Create(3, reexecutions: 2, blockedReads: 2, revalidations: 0),
            delays: [Delay(sharedTx0, LongDelayMs), Delay(sharedTx2, ShortDelayMs)]);

            // Large block with 300 transactions from 3 pre-funded senders
            PrivateKey[] senders = [TestItem.PrivateKeyA, TestItem.PrivateKeyB, TestItem.PrivateKeyC];
            Transaction[] manyTxs = BuildMultiSenderTransactions(senders, txsPerSender: 100, recipientMarker: 0x44);
            yield return Test("300 Transactions from 3 senders", manyTxs,
                // Per-sender nonce chains are pre-ordered; no runtime reexecs.
                ExpectedMetrics.Create(manyTxs.Length, reexecutions: 0));
        }
    }

    public static IEnumerable<TestCaseData> FeeRecipientTransferTests
    {
        get
        {
            const Address emptyFeeCollector = null;

            // GasBeneficiary sends a transaction while receiving fees from all
            yield return new TestCaseData(
                TestItem.AddressA, emptyFeeCollector,
                new[]
                {
                    Tx(TestItem.PrivateKeyA, TestItem.AddressD, 0, 1.Ether()),
                    Tx(TestItem.PrivateKeyB, TestItem.AddressE, 0, 2.Ether()),
                    Tx(TestItem.PrivateKeyC, TestItem.AddressF, 0, 3.Ether()),
                },
                // Distinct recipients and senders; fee keys are per-tx so no shared state.
                ExpectedMetrics.Independent(3),
                NoDelays)
            { TestName = "GasBeneficiary sends one transaction" };

            // GasBeneficiary sends multiple transactions with nonce dependency
            yield return new TestCaseData(
                TestItem.AddressA, emptyFeeCollector,
                new[]
                {
                    Tx(TestItem.PrivateKeyA, TestItem.AddressD, 0, 1.Ether()),
                    Tx(TestItem.PrivateKeyA, TestItem.AddressE, 1, 2.Ether()),
                    Tx(TestItem.PrivateKeyA, TestItem.AddressF, 2, 3.Ether()),
                },
                // Nonce chain is pre-ordered; no runtime reexecs or blocked reads.
                ExpectedMetrics.AllDependent(3),
                NoDelays)
            { TestName = "GasBeneficiary sends multiple transactions with nonce dependency" };

            // FeeCollector sends a transaction while receiving base fees
            Transaction feeCollectorTx0 = Tx(TestItem.PrivateKeyA, TestItem.AddressD, 0, 1.Ether());
            Transaction feeCollectorTx1 = Tx(TestItem.PrivateKeyB, TestItem.AddressE, 0, 2.Ether());  // FeeCollector sends
            Transaction feeCollectorTx2 = Tx(TestItem.PrivateKeyC, AddressG, 0, 3.Ether());
            yield return new TestCaseData(
                TestItem.AddressF, TestItem.AddressB,
                new[]
                {
                    feeCollectorTx0,
                    feeCollectorTx1,
                    feeCollectorTx2,
                },
            // Fee collector sender hits a blocked read until earlier fee credit lands.
            ExpectedMetrics.Create(3, reexecutions: 1, blockedReads: 1, revalidations: 0),
            new[] { Delay(feeCollectorTx0, ShortDelayMs) })
            { TestName = "FeeCollector sends one transaction" };

            // FeeCollector sends multiple transactions with nonce dependency
            yield return new TestCaseData(
                TestItem.AddressF, TestItem.AddressA,
                new[]
                {
                    Tx(TestItem.PrivateKeyA, TestItem.AddressD, 0, 1.Ether()),
                    Tx(TestItem.PrivateKeyA, TestItem.AddressE, 1, 2.Ether()),
                    Tx(TestItem.PrivateKeyA, AddressG, 2, 3.Ether()),
                },
                // Nonce chain is pre-ordered; no runtime reexecs or blocked reads.
                ExpectedMetrics.AllDependent(3),
                NoDelays)
            { TestName = "FeeCollector sends multiple transactions with nonce dependency" };

            // Both GasBeneficiary and FeeCollector send transactions
            Transaction dualFeeTx0 = Tx(TestItem.PrivateKeyA, TestItem.AddressD, 0, 1.Ether());  // GasBeneficiary sends
            Transaction dualFeeTx1 = Tx(TestItem.PrivateKeyB, TestItem.AddressE, 0, 2.Ether());  // FeeCollector sends
            Transaction dualFeeTx2 = Tx(TestItem.PrivateKeyC, TestItem.AddressF, 0, 3.Ether());
            yield return new TestCaseData(
                TestItem.AddressA, TestItem.AddressB,
                new[]
                {
                    dualFeeTx0,
                    dualFeeTx1,
                    dualFeeTx2,
                },
            // Fee collector sender hits a blocked read until earlier fee credit lands.
            ExpectedMetrics.Create(3, reexecutions: 1, blockedReads: 1, revalidations: 0),
            new[] { Delay(dualFeeTx0, ShortDelayMs) })
            { TestName = "Both GasBeneficiary and FeeCollector send transactions" };

            // GasBeneficiary and FeeCollector are the same address
            yield return new TestCaseData(
                TestItem.AddressA, TestItem.AddressA,
                new[]
                {
                    Tx(TestItem.PrivateKeyA, TestItem.AddressD, 0, 1.Ether()),  // The same address gets both fees
                    Tx(TestItem.PrivateKeyB, TestItem.AddressE, 0, 2.Ether()),
                    Tx(TestItem.PrivateKeyC, TestItem.AddressF, 0, 3.Ether()),
                },
                // Fee recipient overlap does not add MVCC deps; recipients are distinct.
                ExpectedMetrics.Independent(3),
                NoDelays)
            { TestName = "GasBeneficiary and FeeCollector are the same address" };

            // GasBeneficiary not involved in any transaction - only receives fees (single Set() per tx)
            yield return new TestCaseData(
                TestItem.AddressD, emptyFeeCollector,
                new[]
                {
                    Tx(TestItem.PrivateKeyA, TestItem.AddressE, 0, 1.Ether()),
                    Tx(TestItem.PrivateKeyB, TestItem.AddressF, 0, 2.Ether()),
                    Tx(TestItem.PrivateKeyC, AddressG, 0, 3.Ether()),
                },
                // Beneficiary never touched; fee keys are per-tx and no recipients overlap.
                ExpectedMetrics.Independent(3),
                NoDelays)
            { TestName = "GasBeneficiary not involved in any transaction" };

            // FeeCollector not involved in any transaction - only receives base fees
            yield return new TestCaseData(
                TestItem.AddressF, TestItem.AddressD,
                new[]
                {
                    Tx(TestItem.PrivateKeyA, TestItem.AddressE, 0, 1.Ether()),
                    Tx(TestItem.PrivateKeyB, AddressG, 0, 2.Ether()),
                    Tx(TestItem.PrivateKeyC, AddressH, 0, 3.Ether()),
                },
                // Fee collector never touched; fee keys are per-tx and no recipients overlap.
                ExpectedMetrics.Independent(3),
                NoDelays)
            { TestName = "FeeCollector not involved in any transaction" };
        }
    }

    [TestCaseSource(nameof(FeeRecipientTransferTests))]
    public async Task Fee_recipient_transfer_tests(Address beneficiary, Address feeCollector, Transaction[] transactions, ExpectedMetrics expectedMetrics, TxDelay[] delays)
    {
        await using DualBlockchain chains = await DualBlockchain.Create(testTimeoutMs: 30_000);
        BlockPair blocks = chains.ProcessBlockWithFeeRecipients(beneficiary, feeCollector, delays, transactions);
        blocks.AssertStateRootsMatch();
        AssertLastBlockMetrics(expectedMetrics);
    }

    public static IEnumerable<TestCaseData> ContractBlocksTests
    {
        get
        {
            // State write then read dependency
            byte[] storeCode = Prepare.EvmCode
                .PushData(42)
                .Op(Instruction.PUSH0)
                .Op(Instruction.SSTORE)
                .STOP()
                .Done;
            byte[] storeInitCode = Prepare.EvmCode.ForInitOf(storeCode).Done;
            Address storeContractAddress = ContractAddress.From(TestItem.AddressA, 0);
            Transaction storeCreate = TxCreateContract(TestItem.PrivateKeyA, 0, storeInitCode);
            Transaction storeCall = TxToContract(TestItem.PrivateKeyB, storeContractAddress, 0, []);
            yield return Test("State write then read dependency",
            [
                storeCreate,
                storeCall
            ],
            // Call reads contract state created earlier, so it revalidates after create.
            ExpectedMetrics.Create(2, reexecutions: 1, blockedReads: 0, revalidations: 1),
            delays: [Delay(storeCreate, ShortDelayMs)]);

            // SelfDestruct in transaction
            byte[] selfDestructCode = Prepare.EvmCode
                .SELFDESTRUCT(TestItem.AddressB)
                .Done;
            byte[] selfDestructInitCode = Prepare.EvmCode.ForInitOf(selfDestructCode).Done;
            Address selfDestructAddress = ContractAddress.From(TestItem.AddressA, 0);
            Transaction selfDestructCreate = TxCreateContract(TestItem.PrivateKeyA, 0, selfDestructInitCode, 10.Ether());
            Transaction selfDestructCall = TxToContract(TestItem.PrivateKeyB, selfDestructAddress, 0, []);
            yield return Test("SelfDestruct in transaction",
            [
                selfDestructCreate,
                selfDestructCall
            ],
            // Selfdestruct reads the newly created contract, so it revalidates after create.
            ExpectedMetrics.Create(2, reexecutions: 1, blockedReads: 0, revalidations: 1),
            delays: [Delay(selfDestructCreate, ShortDelayMs)]);

            // Contract creation with value transfer
            byte[] storeBalanceCode = Prepare.EvmCode
                .Op(Instruction.SELFBALANCE)
                .Op(Instruction.PUSH0)
                .Op(Instruction.SSTORE)
                .STOP()
                .Done;
            byte[] storeBalanceInitCode = Prepare.EvmCode.ForInitOf(storeBalanceCode).Done;
            yield return Test("Contract creation with value transfer",
            [
                TxCreateContract(TestItem.PrivateKeyA, 0, storeBalanceInitCode, 5.Ether()),
                TxCreateContract(TestItem.PrivateKeyB, 0, storeBalanceInitCode, 3.Ether()),
            ], ExpectedMetrics.Independent(2));

            // Transient storage across transactions
            byte[] tStorageCode = Prepare.EvmCode
                .Op(Instruction.PUSH0)
                .Op(Instruction.TLOAD)
                .PushData(1)
                .Op(Instruction.ADD)
                .Op(Instruction.PUSH0)
                .Op(Instruction.TSTORE)
                .Op(Instruction.PUSH0)
                .Op(Instruction.TLOAD)
                .Op(Instruction.PUSH0)
                .Op(Instruction.SSTORE)
                .STOP()
                .Done;
            byte[] tStorageInitCode = Prepare.EvmCode.ForInitOf(tStorageCode).Done;
            Address tStorageAddress = ContractAddress.From(TestItem.AddressA, 0);
            Transaction tStorageCreate = TxCreateContract(TestItem.PrivateKeyA, 0, tStorageInitCode);
            Transaction tStorageCall1 = TxToContract(TestItem.PrivateKeyB, tStorageAddress, 0, []);
            Transaction tStorageCall2 = TxToContract(TestItem.PrivateKeyC, tStorageAddress, 0, []);
            yield return Test("Transient storage across transactions",
            [
                tStorageCreate,
                tStorageCall1,
                tStorageCall2,
            ],
            // Contract creation and shared transient writes reexecute all calls.
            ExpectedMetrics.Create(3, reexecutions: 3, blockedReads: 0, revalidations: 3),
            delays: [Delay(tStorageCreate, ShortDelayMs), Delay(tStorageCall1, ShortDelayMs)]);

            // Contract deployment and immediate call
            byte[] simpleCode = Prepare.EvmCode
                .Op(Instruction.CALLVALUE)
                .Op(Instruction.PUSH0)
                .Op(Instruction.SSTORE)
                .STOP()
                .Done;
            byte[] simpleInitCode = Prepare.EvmCode.ForInitOf(simpleCode).Done;
            Address simpleAddress = ContractAddress.From(TestItem.AddressA, 0);
            Transaction simpleCreate = TxCreateContract(TestItem.PrivateKeyA, 0, simpleInitCode);
            Transaction simpleCall = TxToContract(TestItem.PrivateKeyB, simpleAddress, 0, [], 5.Ether());
            yield return Test("Contract deployment and immediate call",
            [
                simpleCreate,
                simpleCall
            ],
            // Immediate call depends on the contract created in the prior transaction.
            ExpectedMetrics.Create(2, reexecutions: 1, blockedReads: 0, revalidations: 1),
            delays: [Delay(simpleCreate, ShortDelayMs)]);
        }
    }

    public static IEnumerable<TestCaseData> SetCodeBlocksTests
    {
        get
        {
            // SetCode authorization changes nonce
            AuthorizationTuple authBNonce0 = Ecdsa.Sign(TestItem.PrivateKeyB, BlockchainIds.Mainnet, TestItem.AddressC, 0);
            AuthorizationTuple authBNonce1 = Ecdsa.Sign(TestItem.PrivateKeyB, BlockchainIds.Mainnet, TestItem.AddressC, 1);
            Transaction setCodeNonce0 = TxSetCode(TestItem.PrivateKeyA, TestItem.AddressB, 0, [authBNonce0]);
            Transaction setCodeNonce1 = TxSetCode(TestItem.PrivateKeyC, TestItem.AddressD, 0, [authBNonce1]);
            yield return Test("SetCode authorization changes nonce",
            [
                setCodeNonce0,
                setCodeNonce1,
            ],
            // Authority nonce update lands late, so the later auth revalidates once.
            ExpectedMetrics.Create(2, reexecutions: 1, blockedReads: 0, revalidations: 1),
            delays: [Delay(setCodeNonce0, ShortDelayMs)]);

            // Multiple SetCode authorizations same block
            AuthorizationTuple authBMulti0 = Ecdsa.Sign(TestItem.PrivateKeyB, BlockchainIds.Mainnet, TestItem.AddressC, 0);
            AuthorizationTuple authBMulti1 = Ecdsa.Sign(TestItem.PrivateKeyB, BlockchainIds.Mainnet, TestItem.AddressD, 1);
            AuthorizationTuple authBMulti2 = Ecdsa.Sign(TestItem.PrivateKeyB, BlockchainIds.Mainnet, TestItem.AddressE, 2);
            Transaction setCodeMulti0 = TxSetCode(TestItem.PrivateKeyA, TestItem.AddressB, 0, [authBMulti0]);
            Transaction setCodeMulti1 = TxSetCode(TestItem.PrivateKeyC, TestItem.AddressD, 0, [authBMulti1]);
            Transaction setCodeMulti2 = TxSetCode(TestItem.PrivateKeyB, TestItem.AddressF, 2, [authBMulti2]);
            yield return Test("Multiple SetCode authorizations same block",
            [
                setCodeMulti0,
                setCodeMulti1,
                setCodeMulti2,
            ],
            // Sequential auth nonces force tx1/tx2 to revalidate; no blocked reads are needed.
            ExpectedMetrics.Create(3, reexecutions: 3, blockedReads: 0, revalidations: 3),
            delays: [Delay(setCodeMulti0, LongDelayMs), Delay(setCodeMulti1, ShortDelayMs)]);

            // Authorization chain D->E, E->F, F->G
            AuthorizationTuple authChainD = Ecdsa.Sign(TestItem.PrivateKeyD, BlockchainIds.Mainnet, TestItem.AddressE, 0);
            AuthorizationTuple authChainE = Ecdsa.Sign(TestItem.PrivateKeyE, BlockchainIds.Mainnet, TestItem.AddressF, 0);
            AuthorizationTuple authChainF = Ecdsa.Sign(TestItem.PrivateKeyF, BlockchainIds.Mainnet, AddressG, 0);
            Address addressI = TestItem.Addresses[8];
            yield return Test("Authorization chain",
            [
                TxSetCode(TestItem.PrivateKeyA, AddressG, 0, [authChainD]),
                TxSetCode(TestItem.PrivateKeyB, AddressH, 0, [authChainE]),
                TxSetCode(TestItem.PrivateKeyC, addressI, 0, [authChainF]),
            ],
            ExpectedMetrics.Independent(3));

            // Re-delegation: delegate B to C, then B to D
            AuthorizationTuple authRedelegate0 = Ecdsa.Sign(TestItem.PrivateKeyB, BlockchainIds.Mainnet, TestItem.AddressC, 0);
            AuthorizationTuple authRedelegate1 = Ecdsa.Sign(TestItem.PrivateKeyB, BlockchainIds.Mainnet, TestItem.AddressD, 1);
            Transaction redelegate0 = TxSetCode(TestItem.PrivateKeyA, TestItem.AddressB, 0, [authRedelegate0]);
            Transaction redelegate1 = TxSetCode(TestItem.PrivateKeyC, TestItem.AddressB, 0, [authRedelegate1]);
            yield return Test("Re-delegation in same block",
            [
                redelegate0,
                redelegate1,
            ],
            // Second delegation must observe the updated authority nonce from the first.
            ExpectedMetrics.Create(2, reexecutions: 1, blockedReads: 0, revalidations: 1),
            delays: [Delay(redelegate0, ShortDelayMs)]);
        }
    }

    private static Transaction Tx(
        PrivateKey from,
        Address to,
        UInt256 nonce,
        UInt256? value = null,
        long gasLimit = 1_000_000,
        byte[] data = null) =>
        Build.A.Transaction
            .WithType(TxType.EIP1559)
            .To(to)
            .WithNonce(nonce)
            .WithChainId(BlockchainIds.Mainnet)
            .WithMaxFeePerGas(2.GWei())
            .WithMaxPriorityFeePerGas(1.GWei())
            .WithValue(value ?? 1.Ether())
            .WithGasLimit(gasLimit)
            .WithData(data ?? [])
            .SignedAndResolved(from, false)
            .TestObject;

    // MaxFeePerGas * GasLimit must exceed sender balance (1000 ETH)
    // 50_000_000 GWei = 0.05 ETH per gas, * 21000 gas = 1050 ETH > 1000 ETH
    private static Transaction Tx1559WithHighMaxFee(PrivateKey from, Address to, UInt256 nonce) =>
        Build.A.Transaction
            .WithType(TxType.EIP1559)
            .To(to)
            .WithNonce(nonce)
            .WithChainId(BlockchainIds.Mainnet)
            .WithMaxFeePerGas(50_000_000.GWei())
            .WithMaxPriorityFeePerGas(1.GWei())
            .WithGasLimit(21000)
            .SignedAndResolved(from, false)
            .WithValue(1.Wei())
            .TestObject;

    // MinerPremiumNegative is triggered when MaxFeePerGas < BaseFee (1 GWei)
    // Setting MaxFeePerGas to 0 makes it impossible to cover the base fee
    private static Transaction Tx1559WithNegativePremium(PrivateKey from, Address to, UInt256 nonce) =>
        Build.A.Transaction
            .WithType(TxType.EIP1559)
            .To(to)
            .WithNonce(nonce)
            .WithChainId(BlockchainIds.Mainnet)
            .WithMaxFeePerGas(0)
            .WithMaxPriorityFeePerGas(1.GWei())
            .WithGasLimit(21000)
            .SignedAndResolved(from, false)
            .WithValue(1.Wei())
            .TestObject;

    private static Transaction TxWithoutSender(Address to, UInt256 nonce) =>
        Build.A.Transaction
            .To(to)
            .WithNonce(nonce)
            .WithChainId(BlockchainIds.Mainnet)
            .WithGasLimit(21000)
            .WithValue(1.Wei())
            .TestObject;

    private static Transaction TxToContract(PrivateKey from, Address to, UInt256 nonce, byte[] data, UInt256? value = null, long gasLimit = 100_000) =>
        Build.A.Transaction
            .To(to)
            .WithNonce(nonce)
            .WithChainId(BlockchainIds.Mainnet)
            .WithGasLimit(gasLimit)
            .WithData(data)
            .SignedAndResolved(from, false)
            .WithValue(value ?? 0)
            .TestObject;

    private static Transaction TxCreateContract(PrivateKey from, UInt256 nonce, byte[] initCode, UInt256? value = null) =>
        Build.A.Transaction
            .WithNonce(nonce)
            .WithChainId(BlockchainIds.Mainnet)
            .WithGasLimit(1_000_000)
            .WithCode(initCode)
            .SignedAndResolved(from, false)
            .WithValue(value ?? 0)
            .TestObject;

    private static Transaction TxSetCode(PrivateKey from, Address to, UInt256 nonce, AuthorizationTuple[] authList) =>
        Build.A.Transaction
            .WithType(TxType.SetCode)
            .To(to)
            .WithNonce(nonce)
            .WithChainId(BlockchainIds.Mainnet)
            .WithMaxFeePerGas(1.GWei())
            .WithMaxPriorityFeePerGas(1.GWei())
            .WithGasLimit(100_000)
            .WithAuthorizationCode(authList)
            .SignedAndResolved(from, false)
            .WithValue(0)
            .TestObject;

    private static TestCaseData Test(string name, Transaction[] transactions, ExpectedMetrics expectedMetrics, TxDelay[] delays = null) =>
        new([transactions, expectedMetrics, delays]) { TestName = name };

    private static TestCaseData Test(
        Transaction[] transactions,
        TransactionResult expected,
        ExpectedMetrics expectedMetrics,
        string name = "",
        TxDelay[] delays = null,
        [CallerArgumentExpression(nameof(expected))] string error = "") =>
        new(transactions, expected, expectedMetrics, delays ?? []) { TestName = $"{transactions.Length} Transactions, {error.Replace(nameof(TransactionResult) + ".", "")}:{name}" };

    [TestCaseSource(nameof(SimpleBlocksTests))]
    [TestCaseSource(nameof(ContractBlocksTests))]
    public async Task Successful_blocks(Transaction[] transactions, ExpectedMetrics expectedMetrics, TxDelay[] delays)
    {
        await using DualBlockchain chains = await DualBlockchain.Create(testTimeoutMs: 30_000);
        BlockPair blocks = delays is null ? await chains.AddBlock(transactions) : await chains.AddBlockWithDelays(delays, transactions);
        blocks.AssertFullMatch(transactions.Length);
        AssertLastBlockMetrics(expectedMetrics);
    }

    [TestCaseSource(nameof(SetCodeBlocksTests))]
    public async Task Successful_setcode_blocks_direct(Transaction[] transactions, ExpectedMetrics expectedMetrics, TxDelay[] delays)
    {
        await using DualBlockchain chains = await DualBlockchain.Create(testTimeoutMs: 30_000);
        BlockPair blocks = chains.ProcessBlockDirectOnHead(delays ?? [], transactions);
        blocks.AssertFullMatch(transactions.Length);
        AssertLastBlockMetrics(expectedMetrics);
    }

    [Test]
    public async Task Multiple_senders_with_state_dependencies_direct()
    {
        byte[] storeCallerCode = Prepare.EvmCode
            .Op(Instruction.CALLER)
            .Op(Instruction.DUP1)
            .Op(Instruction.SLOAD)
            .PushData(1)
            .Op(Instruction.ADD)
            .Op(Instruction.SWAP1)
            .Op(Instruction.SSTORE)
            .STOP()
            .Done;
        byte[] storeCallerInitCode = Prepare.EvmCode.ForInitOf(storeCallerCode).Done;
        Address storeCallerAddress = ContractAddress.From(TestItem.AddressA, 0);
        Transaction storeCallerCreate = TxCreateContract(TestItem.PrivateKeyA, 0, storeCallerInitCode);
        Transaction storeCallerCall1 = TxToContract(TestItem.PrivateKeyB, storeCallerAddress, 0, []);
        Transaction storeCallerCall2 = TxToContract(TestItem.PrivateKeyC, storeCallerAddress, 0, []);
        Transaction[] transactions = [storeCallerCreate, storeCallerCall1, storeCallerCall2];

        await using DualBlockchain chains = await DualBlockchain.Create(testTimeoutMs: 30_000);
        BlockPair blocks = chains.ProcessBlockDirectOnHead(
            [Delay(storeCallerCall1, LongDelayMs), Delay(storeCallerCall2, LongDelayMs)],
            transactions);
        blocks.AssertFullMatch(transactions.Length);
        // Calls are delayed so the create commit lands before they read the contract.
        AssertLastBlockMetrics(ExpectedMetrics.Create(3, reexecutions: 0));
    }

    [Test]
    public async Task Different_sender_transactions_are_parallelizable()
    {
        // Two blocks: fund senders first, then execute independent transactions.
        const int senderCount = 300;
        PrivateKey[] seedSenders = [TestItem.PrivateKeyA, TestItem.PrivateKeyB, TestItem.PrivateKeyC];
        (Transaction[] fundingTxs, Transaction[] independentTxs) = BuildSeededSenderBlocks(senderCount, seedSenders);

        await using DualBlockchain chains = await DualBlockchain.Create(testTimeoutMs: 60_000);

        BlockPair fundingBlocks = await chains.AddBlock(fundingTxs);
        fundingBlocks.AssertFullMatch(fundingTxs.Length);
        // Nonce chains are pre-ordered; no runtime reexecs or blocked reads.
        AssertLastBlockMetrics(ExpectedMetrics.Create(fundingTxs.Length, reexecutions: 0));

        BlockPair independentBlocks = chains.ProcessBlockDirect(fundingBlocks, independentTxs);
        independentBlocks.AssertFullMatch(independentTxs.Length);
        // Senders are pre-funded, so the block is fully independent.
        AssertLastBlockMetrics(ExpectedMetrics.Independent(independentTxs.Length));
    }

    [Test]
    public async Task Expanding_senders_are_parallelizable_direct()
    {
        PrivateKey[] seedSenders = [TestItem.PrivateKeyA, TestItem.PrivateKeyB, TestItem.PrivateKeyC];
        const int newSendersPerSeed = 20;
        (Transaction[] transactions, TxDelay[] delays) = BuildExpandingSenderTransactions(seedSenders, newSendersPerSeed, LongDelayMs);

        await using DualBlockchain chains = await DualBlockchain.Create(testTimeoutMs: 30_000);
        BlockPair blocks = chains.ProcessBlockDirectOnHead(delays, transactions);
        blocks.AssertFullMatch(transactions.Length);
        // Spending txs are delayed to execute after the funding wave commits.
        AssertLastBlockMetrics(ExpectedMetrics.Create(transactions.Length, reexecutions: 0));
    }

    private static (Transaction[] funding, Transaction[] independent) BuildSeededSenderBlocks(int senderCount, PrivateKey[] seedSenders)
    {
        Transaction[] fundingTxs = new Transaction[senderCount];
        Transaction[] independentTxs = new Transaction[senderCount];
        for (int i = 0; i < senderCount; i++)
        {
            PrivateKey seedSender = seedSenders[i % seedSenders.Length];
            UInt256 nonce = (UInt256)(i / seedSenders.Length);
            PrivateKey seededSender = CreateSeededSender(i);
            fundingTxs[i] = Tx(seedSender, seededSender.Address, nonce, 2.Ether(), gasLimit: 21_000);

            byte[] recipientBytes = new byte[Address.Size];
            recipientBytes[0] = 0x55;
            recipientBytes[1] = 0xAA;
            recipientBytes[2] = (byte)(i >> 8);
            recipientBytes[3] = (byte)i;
            independentTxs[i] = Tx(seededSender, new Address(recipientBytes), 0, 1.Ether(), gasLimit: 21_000);
        }

        return (fundingTxs, independentTxs);
    }

    private static Transaction[] BuildMultiSenderTransactions(PrivateKey[] senders, int txsPerSender, byte recipientMarker)
    {
        Transaction[] transactions = new Transaction[senders.Length * txsPerSender];
        for (int senderIndex = 0; senderIndex < senders.Length; senderIndex++)
        {
            for (int nonce = 0; nonce < txsPerSender; nonce++)
            {
                int index = senderIndex * txsPerSender + nonce;
                Address recipient = CreateRecipientAddress(recipientMarker, index);
                transactions[index] = Tx(senders[senderIndex], recipient, (UInt256)nonce, 1.Wei(), 21_000);
            }
        }

        return transactions;
    }

    private static (Transaction[] transactions, TxDelay[] delays) BuildExpandingSenderTransactions(PrivateKey[] seedSenders, int newSendersPerSeed, int spendDelayMs)
    {
        int totalNewSenders = seedSenders.Length * newSendersPerSeed;
        List<Transaction> transactions = new(totalNewSenders * 2);
        List<TxDelay> delays = spendDelayMs > 0 ? new List<TxDelay>(totalNewSenders) : new List<TxDelay>();
        List<PrivateKey> fundedSenders = new(totalNewSenders);
        int senderIndex = 0;

        for (int seedIndex = 0; seedIndex < seedSenders.Length; seedIndex++)
        {
            PrivateKey seedSender = seedSenders[seedIndex];
            for (int nonce = 0; nonce < newSendersPerSeed; nonce++)
            {
                PrivateKey fundedSender = CreateSeededSender(senderIndex++);
                fundedSenders.Add(fundedSender);
                transactions.Add(Tx(seedSender, fundedSender.Address, (UInt256)nonce, 2.Ether(), 21_000));
            }
        }

        for (int i = 0; i < fundedSenders.Count; i++)
        {
            Transaction spendTx = Tx(fundedSenders[i], CreateRecipientAddress(0x66, i), 0, 1.Ether(), 21_000);
            transactions.Add(spendTx);
            if (spendDelayMs > 0)
            {
                delays.Add(Delay(spendTx, spendDelayMs));
            }
        }

        return (transactions.ToArray(), delays.Count == 0 ? [] : delays.ToArray());
    }

    private static PrivateKey CreateSeededSender(int index)
    {
        byte[] keyBytes = new byte[32];
        ((UInt256)(index + 1)).ToBigEndian(keyBytes);
        return new PrivateKey(keyBytes);
    }

    private static Address CreateRecipientAddress(byte marker, int index)
    {
        byte[] recipientBytes = new byte[Address.Size];
        recipientBytes[0] = marker;
        recipientBytes[1] = (byte)(index >> 8);
        recipientBytes[2] = (byte)index;
        recipientBytes[3] = (byte)(index >> 16);
        return new Address(recipientBytes);
    }

    private static Block ProcessBlockDirect(ParallelTestBlockchain chain, BlockHeader parent, Transaction[] transactions, long gasLimit)
    {
        Block block = Build.A.Block
            .WithParent(parent)
            .WithGasLimit(gasLimit)
            .WithTransactions(transactions)
            .TestObject;
        IReleaseSpec spec = chain.SpecProvider.GetSpec(block.Header);
        return chain.MainProcessingContext.BlockProcessor.ProcessOne(block, ProcessingOptions.NoValidation, NullBlockTracer.Instance, spec).Block;
    }

    public static IEnumerable<TestCaseData> FailedBlocksTests
    {
        get
        {
            yield return Test([Tx(TestItem.PrivateKeyA, TestItem.AddressB, 1)],
                TransactionResult.TransactionNonceTooHigh, ExpectedMetrics.Independent(1),
                "nonce too high");

            yield return Test(
            [
                Tx(TestItem.PrivateKeyA, TestItem.AddressB, 0),
                Tx(TestItem.PrivateKeyA, TestItem.AddressB, 2)
            ],
            TransactionResult.TransactionNonceTooHigh,
            // Nonce chain is pre-ordered; no runtime reexecs or blocked reads.
            ExpectedMetrics.AllDependent(2),
            "nonce gap on dependent transaction");

            yield return Test(
            [
                Tx(TestItem.PrivateKeyA, TestItem.AddressB, 0),
                Tx(TestItem.PrivateKeyA, TestItem.AddressB, 0)
            ],
            TransactionResult.TransactionNonceTooLow,
            // Nonce chain is pre-ordered; no runtime reexecs or blocked reads.
            ExpectedMetrics.AllDependent(2),
            "nonce reuse on dependent transaction");

            AuthorizationTuple auth = Ecdsa.Sign(TestItem.PrivateKeyB, BlockchainIds.Mainnet, TestItem.AddressC, 0);
            Transaction setCodeNonceReuse = TxSetCode(TestItem.PrivateKeyA, TestItem.AddressB, 0, [auth]);
            Transaction delegatedNonceReuse = Tx(TestItem.PrivateKeyB, TestItem.AddressC, 0);
            yield return Test(
            [
                setCodeNonceReuse,
                delegatedNonceReuse,
            ],
            TransactionResult.TransactionNonceTooLow,
            // Delegation nonce moves after SetCode, forcing the delegated tx to revalidate.
            ExpectedMetrics.Create(2, reexecutions: 1, blockedReads: 0, revalidations: 1),
            "nonce reuse of SetCode authorization",
            delays: [Delay(setCodeNonceReuse, ShortDelayMs)]);

            yield return Test([Tx(TestItem.PrivateKeyF, TestItem.AddressB, 0)],
                TransactionResult.InsufficientSenderBalance, ExpectedMetrics.Independent(1));

            yield return Test([
                Tx(TestItem.PrivateKeyA, TestItem.AddressB, 0),
                Tx(TestItem.PrivateKeyF, TestItem.AddressB, 0)
            ], TransactionResult.InsufficientSenderBalance, ExpectedMetrics.Independent(2),
                "insufficient balance on second transaction");

            yield return Test([Tx(TestItem.PrivateKeyA, TestItem.AddressB, 0, 1.Ether(), 100)],
                TransactionResult.GasLimitBelowIntrinsicGas, ExpectedMetrics.Independent(1),
                "insufficient intrinsic gas limit");

            yield return Test([TxWithoutSender(TestItem.AddressB, 0)],
                TransactionResult.SenderNotSpecified, ExpectedMetrics.Independent(1),
                "sender not specified");

            // InsufficientMaxFeePerGasForSenderBalance - EIP-1559 tx
            yield return Test([Tx1559WithHighMaxFee(TestItem.PrivateKeyA, TestItem.AddressB, 0)],
                TransactionResult.InsufficientMaxFeePerGasForSenderBalance, ExpectedMetrics.Independent(1),
                "insufficient max fee per gas for sender balance");

            // MinerPremiumNegative - maxPriorityFeePerGas > maxFeePerGas
            yield return Test([Tx1559WithNegativePremium(TestItem.PrivateKeyA, TestItem.AddressB, 0)],
                TransactionResult.MinerPremiumNegative, ExpectedMetrics.Independent(1),
                "miner premium negative");

            // TransactionSizeOverMaxInitCodeSize - EIP-3860 (init code > 49152 bytes)
            yield return Test([TxCreateContract(TestItem.PrivateKeyA, 0, new byte[50000])],
                TransactionResult.TransactionSizeOverMaxInitCodeSize, ExpectedMetrics.Independent(1),
                "transaction size over max init code size");

            // B has 1000 ETH. tx1 sends 999.5 ETH leaving ~0.5 ETH. tx2 tries to send 1 ETH and fails.
            yield return Test(
            [
                Tx(TestItem.PrivateKeyB, TestItem.AddressC, 0, 999.Ether() + 500000000.GWei()),
                Tx(TestItem.PrivateKeyB, TestItem.AddressC, 1, 1.Ether()),
            ],
            TransactionResult.InsufficientSenderBalance,
            // Nonce chain is pre-ordered; no runtime reexecs or blocked reads.
            ExpectedMetrics.AllDependent(2),
            "insufficient balance on dependent transaction");

            // NonceOverflow - contract creation with max nonce
            yield return Test([TxCreateContract(TestItem.PrivateKeyA, ulong.MaxValue, [0x60, 0x00, 0x60, 0x00, 0xF3])],
                TransactionResult.NonceOverflow, ExpectedMetrics.Independent(1),
                "nonce overflow on contract creation");
        }
    }

    [TestCaseSource(nameof(FailedBlocksTests))]
    public async Task Failed_blocks(Transaction[] transactions, TransactionResult expected, ExpectedMetrics expectedMetrics, TxDelay[] delays)
    {
        using ParallelTestBlockchain parallel = await ParallelTestBlockchain.Create(BuildConfig(true));
        BlockHeader head = parallel.BlockTree.Head!.Header;
        Block block = Build.A.Block
            .WithTransactions(transactions)
            .WithParent(head)
            .WithBaseFeePerGas(1.GWei())
            .TestObject;
        IReleaseSpec releaseSpec = parallel.SpecProvider.GetSpec(block.Header);
        using IDisposable scope = parallel.MainProcessingContext.WorldState.BeginScope(head);
        TransactionResult result = TransactionResult.Ok;
        try
        {
            parallel.DelayPolicy.SetDelays(delays);
            // Use NoValidation to skip block header validation (gas, state root, etc.)
            // since we're testing transaction validation, not block validation
            parallel.MainProcessingContext.BlockProcessor.ProcessOne(block, ProcessingOptions.NoValidation, NullBlockTracer.Instance, releaseSpec);
        }
        catch (InvalidTransactionException e)
        {
            result = e.Reason;
        }

        Assert.That(result, Is.EqualTo(expected));
        AssertLastBlockMetrics(expectedMetrics);
    }

    [Test]
    public async Task Failed_block_gas_limit_exceeded()
    {
        using ParallelTestBlockchain parallel = await ParallelTestBlockchain.Create(BuildConfig(true));
        BlockHeader head = parallel.BlockTree.Head!.Header;

        Transaction tx = Tx(TestItem.PrivateKeyA, TestItem.AddressB, 0, 1.Ether(), 100_000);
        Block block = Build.A.Block
            .WithTransactions(tx)
            .WithParent(head)
            .WithGasLimit(21000)
            .TestObject;

        IReleaseSpec releaseSpec = parallel.SpecProvider.GetSpec(block.Header);
        using IDisposable scope = parallel.MainProcessingContext.WorldState.BeginScope(head);
        TransactionResult result = TransactionResult.Ok;
        try
        {
            parallel.MainProcessingContext.BlockProcessor.ProcessOne(block, ProcessingOptions.None, NullBlockTracer.Instance, releaseSpec);
        }
        catch (InvalidTransactionException e)
        {
            result = e.Reason;
        }

        Assert.That(result, Is.EqualTo(TransactionResult.BlockGasLimitExceeded));
        AssertLastBlockMetrics(ExpectedMetrics.Independent(1));
    }

    [Test]
    public async Task SelfDestruct_recreate_in_same_transaction()
    {
        await using DualBlockchain chains = await DualBlockchain.Create();

        byte[] selfDestructCode = Prepare.EvmCode
            .SELFDESTRUCT(TestItem.AddressB)
            .Done;
        byte[] initCode = Prepare.EvmCode.ForInitOf(selfDestructCode).Done;

        byte[] salt = new UInt256(123).ToBigEndian();
        Address createAddress = ContractAddress.From(TestItem.AddressA, 0);
        Address contractAddress = ContractAddress.From(createAddress, salt, initCode);

        byte[] create2Code = Prepare.EvmCode
            .Create2(initCode, salt, 1.Ether())
            .Call(contractAddress, 50000)
            .Create2(initCode, salt, 1.Ether())
            .STOP()
            .Done;
        byte[] create2InitCode = Prepare.EvmCode.ForInitOf(create2Code).Done;

        BlockPair blocks = await chains.AddBlock(TxCreateContract(TestItem.PrivateKeyA, 0, create2InitCode, 10.Ether()));
        blocks.AssertFullMatch(1);
        AssertLastBlockMetrics(ExpectedMetrics.Independent(1));
    }

    [Test]
    public async Task Cross_contract_calls_with_value()
    {
        await using DualBlockchain chains = await DualBlockchain.Create();

        byte[] receiverCode = Prepare.EvmCode
            .Op(Instruction.CALLVALUE)
            .Op(Instruction.PUSH0)
            .Op(Instruction.SSTORE)
            .STOP()
            .Done;
        byte[] receiverInitCode = Prepare.EvmCode.ForInitOf(receiverCode).Done;
        Address receiverAddress = ContractAddress.From(TestItem.AddressA, 0);

        byte[] callerCode = Prepare.EvmCode
            .CallWithValue(receiverAddress, 50000)
            .STOP()
            .Done;
        byte[] callerInitCode = Prepare.EvmCode.ForInitOf(callerCode).Done;

        Transaction receiverCreate = TxCreateContract(TestItem.PrivateKeyA, 0, receiverInitCode);
        Transaction callerCreate = TxCreateContract(TestItem.PrivateKeyB, 0, callerInitCode, 5.Ether());
        Transaction callerCall = TxToContract(TestItem.PrivateKeyB, ContractAddress.From(TestItem.AddressB, 0), 1, [], 2.Ether());
        BlockPair blocks = await chains.AddBlockWithDelays(
            [Delay(receiverCreate, ShortDelayMs)],
            receiverCreate,
            callerCreate,
            callerCall
        );
        blocks.AssertFullMatch(3);
        // Receiver creation lands after the call, forcing a revalidation.
        AssertLastBlockMetrics(ExpectedMetrics.Create(3, reexecutions: 1, blockedReads: 0, revalidations: 1));
    }

    [Test]
    public async Task Delegated_account_can_send_transactions()
    {
        using ParallelTestBlockchain parallel = await ParallelTestBlockchain.Create(BuildConfig(true));

        // First block: SetCode authorization that delegates PrivateKeyB to AddressC
        AuthorizationTuple auth = Ecdsa.Sign(TestItem.PrivateKeyB, BlockchainIds.Mainnet, TestItem.AddressC, 0);
        Transaction setCodeTx = TxSetCode(TestItem.PrivateKeyA, TestItem.AddressB, 0, [auth]);
        Block setCodeBlock = await parallel.AddBlock(setCodeTx);

        Assert.That(setCodeBlock.Transactions, Has.Length.EqualTo(1), "SetCode transaction should be included");
        AssertLastBlockMetrics(ExpectedMetrics.Independent(1));

        // Second block: Transaction from the delegated account (PrivateKeyB with nonce=1 after authorization)
        Transaction txFromB = Tx(TestItem.PrivateKeyB, TestItem.AddressC, 1, 1.Ether(), 100_000);

        Block txBlock = await parallel.AddBlock(txFromB);

        Assert.That(txBlock.Transactions, Has.Length.EqualTo(1), "Transaction from delegated account should be included");
        AssertLastBlockMetrics(ExpectedMetrics.Independent(1));
    }

    [Test]
    public async Task Empty_block_parallel()
    {
        await using DualBlockchain chains = await DualBlockchain.Create();
        BlockPair blocks = await chains.AddBlock();
        blocks.AssertFullMatch(0);
        AssertLastBlockMetrics(ExpectedMetrics.Independent(0));
    }

    [Test]
    public async Task Storage_conflicts_with_setup_block()
    {
        // Tests WAW (Write-After-Write) conflicts requiring re-execution
        await using DualBlockchain chains = await DualBlockchain.Create();

        // Contract that increments slot 0
        byte[] incrementerCode = Prepare.EvmCode
            .Op(Instruction.PUSH0)
            .Op(Instruction.SLOAD)
            .PushData(1)
            .Op(Instruction.ADD)
            .Op(Instruction.PUSH0)
            .Op(Instruction.SSTORE)
            .STOP()
            .Done;
        byte[] incrementerInitCode = Prepare.EvmCode.ForInitOf(incrementerCode).Done;

        await chains.AddBlock(TxCreateContract(TestItem.PrivateKeyA, 0, incrementerInitCode));

        Address incrementerAddress = ContractAddress.From(TestItem.AddressA, 0);

        // 3 transactions from different senders all incrementing the same counter
        Transaction conflictTx0 = TxToContract(TestItem.PrivateKeyA, incrementerAddress, 1, []);
        Transaction conflictTx1 = TxToContract(TestItem.PrivateKeyB, incrementerAddress, 0, []);
        Transaction conflictTx2 = TxToContract(TestItem.PrivateKeyC, incrementerAddress, 0, []);
        Transaction[] transactions = [conflictTx0, conflictTx1, conflictTx2];

        BlockPair blocks = await chains.AddBlockWithDelays(
            [Delay(conflictTx0, LongDelayMs), Delay(conflictTx1, ShortDelayMs)],
            transactions
        );
        blocks.AssertStateRootsMatch();
        // All txs write the same storage slot, so they reexecute with a blocked read.
        AssertLastBlockMetrics(ExpectedMetrics.Create(3, reexecutions: 4, blockedReads: 1, revalidations: 3));
    }

    [Test]
    public async Task CREATE2_collision_with_setup_block()
    {
        await using DualBlockchain chains = await DualBlockchain.Create();

        byte[] simpleCode = Prepare.EvmCode
            .PushData(1)
            .Op(Instruction.PUSH0)
            .Op(Instruction.SSTORE)
            .STOP()
            .Done;
        byte[] initCode = Prepare.EvmCode.ForInitOf(simpleCode).Done;
        byte[] salt = new UInt256(12345).ToBigEndian();

        byte[] factoryCode = Prepare.EvmCode
            .Create2(initCode, salt, 0)
            .STOP()
            .Done;
        byte[] factoryInitCode = Prepare.EvmCode.ForInitOf(factoryCode).Done;

        await chains.AddBlock(
            TxCreateContract(TestItem.PrivateKeyA, 0, factoryInitCode),
            TxCreateContract(TestItem.PrivateKeyB, 0, factoryInitCode)
        );

        Address factory1 = ContractAddress.From(TestItem.AddressA, 0);
        Address factory2 = ContractAddress.From(TestItem.AddressB, 0);

        BlockPair blocks = await chains.AddBlock(
            TxToContract(TestItem.PrivateKeyA, factory1, 1, []),
            TxToContract(TestItem.PrivateKeyB, factory2, 1, [])
        );
        blocks.AssertStateRootsMatch();
        AssertLastBlockMetrics(ExpectedMetrics.Independent(2));
    }

    [Test]
    public async Task SelfDestruct_with_Shanghai_spec()
    {
        // Complex SELFDESTRUCT scenario in Shanghai spec:
        // In a SINGLE block:
        // tx0: Deploy factory contract
        // tx1: Factory deploys contract via CREATE2 with storage
        // tx2: Call contract to self-destruct
        // tx3: Factory re-deploys contract at SAME address via CREATE2
        // tx4: Verify the re-deployed contract has fresh storage
        await using DualBlockchain chains = await DualBlockchain.Create(Shanghai.Instance);

        // Contract that stores a value and can self-destruct
        // Constructor stores CALLVALUE at slot 0
        // When called, if calldata[0] == 1, self-destruct; otherwise return storage[0]
        byte[] destructibleCode = Prepare.EvmCode
            // Check if calldata[0] == 1
            .CALLDATALOAD(0)
            .PushData(1)
            .Op(Instruction.EQ)
            .PushData(20) // jump destination for self-destruct
            .Op(Instruction.JUMPI)
            // Return storage[0]
            .SLOAD(0)
            .MSTORE(0)
            .RETURN(0, 32)
            // Self-destruct path
            .JUMPDEST()
            .SELFDESTRUCT(TestItem.AddressB)
            .Done;

        // Init code stores CALLVALUE at slot 0, then deploys the code
        byte[] destructibleInitCode = Prepare.EvmCode
            .CALLVALUE()
            .SSTORE(0)
            .ForInitOf(destructibleCode)
            .Done;

        byte[] salt = new UInt256(42).ToBigEndian();

        // Factory contract that creates the destructible contract via CREATE2
        // Uses CALLVALUE so the ETH sent to factory is passed to the created contract
        byte[] factoryCode = Prepare.EvmCode
            .StoreDataInMemory(0, destructibleInitCode)
            .PushData(salt)
            .PushData(destructibleInitCode.Length)
            .PushData(0) // memory position
            .CALLVALUE()
            .Op(Instruction.CREATE2)
            .STOP()
            .Done;
        byte[] factoryInitCode = Prepare.EvmCode.ForInitOf(factoryCode).Done;

        // Deploy factory in setup block
        await chains.AddBlock(TxCreateContract(TestItem.PrivateKeyA, 0, factoryInitCode));

        Address factoryAddress = ContractAddress.From(TestItem.AddressA, 0);
        Address create2Address = ContractAddress.From(factoryAddress, salt, Keccak.Compute(destructibleInitCode).Bytes);

        // Test block with multiple operations on the same CREATE2 address:
        // Using different senders to test parallel execution without nonce dependencies
        Transaction factoryCreate = Tx(TestItem.PrivateKeyA, factoryAddress, 1, 5.Ether(), 500_000);
        Transaction contractDestroy = Tx(TestItem.PrivateKeyB, create2Address, 0, 0, 100_000, new UInt256(1).ToBigEndian());
        Transaction factoryRecreate = Tx(TestItem.PrivateKeyC, factoryAddress, 0, 7.Ether(), 500_000);
        BlockPair blocks = await chains.AddBlockWithDelays(
            [Delay(factoryCreate, ShortDelayMs), Delay(factoryRecreate, LongDelayMs)],
            // tx0 (sender A): Factory creates contract with 5 ETH (stored in slot 0)
            factoryCreate,
            // tx1 (sender B): Self-destruct the contract (calldata = 1)
            // Different sender - no nonce dependency, tests parallel state tracking
            contractDestroy,
            // tx2 (sender C): Factory re-creates contract with 7 ETH (should have fresh storage)
            // Different sender - tests that re-creation sees the self-destructed state
            factoryRecreate
        );
        blocks.AssertStateRootsMatch();
        // CREATE2 reuse is observed in-order here; no reexecs recorded.
        AssertLastBlockMetrics(ExpectedMetrics.Independent(3));
    }

    [Test]
    public async Task Failed_sender_has_deployed_code()
    {
        // EIP-3607: Reject transactions from senders with deployed code
        using ParallelTestBlockchain parallel = await ParallelTestBlockchain.Create(BuildConfig(true));
        BlockHeader head = parallel.BlockTree.Head!.Header;
        IReleaseSpec releaseSpec = parallel.SpecProvider.GetSpec(head);

        // Insert code at TestItem.AddressA (an EOA)
        byte[] code = [0x60, 0x00, 0xF3]; // PUSH 0, RETURN - simple contract code
        Hash256 codeHash = Keccak.Compute(code);

        using IDisposable scope = parallel.MainProcessingContext.WorldState.BeginScope(head);
        parallel.MainProcessingContext.WorldState.InsertCode(TestItem.AddressA, codeHash.ValueHash256, code, releaseSpec);
        parallel.MainProcessingContext.WorldState.Commit(releaseSpec, NullStateTracer.Instance, commitRoots: true);

        // Now try to send a transaction from that address
        Transaction tx = Tx(TestItem.PrivateKeyA, TestItem.AddressB, 0, 1.Ether());
        Block block = Build.A.Block
            .WithTransactions(tx)
            .WithParent(head)
            .WithBaseFeePerGas(1.GWei())
            .TestObject;

        TransactionResult result = TransactionResult.Ok;
        try
        {
            OverridableReleaseSpec spec = (OverridableReleaseSpec)parallel.SpecProvider.GetSpec(block.Header);
            spec.IsEip3607Enabled = true;
            parallel.MainProcessingContext.BlockProcessor.ProcessOne(block, ProcessingOptions.None, NullBlockTracer.Instance, spec);
        }
        catch (InvalidTransactionException e)
        {
            result = e.Reason;
        }

        Assert.That(result, Is.EqualTo(TransactionResult.SenderHasDeployedCode));
        AssertLastBlockMetrics(ExpectedMetrics.Independent(1));
    }

    [Test]
    public async Task Reexecution_when_slow_contract_provides_balance()
    {
        // Test that demonstrates the Block-STM re-execution mechanism:
        // tx0: Deploy contract that does expensive work then transfers balance to recipient
        // tx1: Call contract with 10 ETH, recipient = F (slow, expensive hashing)
        // tx2: Simple transfer from F to B (fast, but F has no initial balance)
        // Expected: tx2 initially sees F's balance = 0, after tx1 completes F has balance, tx2 re-executes and succeeds

        // Build the expensive transfer contract with 100 keccak operations
        Prepare slowTransferBuilder = Prepare.EvmCode
            .MSTORE(0, new byte[32]);
        for (int i = 0; i < 100; i++)
        {
            slowTransferBuilder
                .KECCAK256(0, 32)
                .MSTORE(0);
        }
        byte[] transferCode = slowTransferBuilder
            .PushData(0)        // retLength
            .PushData(0)        // retOffset
            .PushData(0)        // argsLength
            .PushData(0)        // argsOffset
            .SELFBALANCE()      // value = contract balance
            .CALLDATALOAD(0)    // addr = recipient from calldata
            .GAS()              // gas = remaining gas
            .Op(Instruction.CALL)
            .STOP()
            .Done;
        byte[] transferInitCode = Prepare.EvmCode.ForInitOf(transferCode).Done;
        Address transferContract = ContractAddress.From(TestItem.AddressB, 0);

        // Setup block: Deploy the contract
        Transaction deployTx = TxCreateContract(TestItem.PrivateKeyB, 0, transferInitCode);

        // Test block transactions:
        // tx0: Call contract with 10 ETH, transfers to F after expensive work
        Transaction callContractTx = Tx(TestItem.PrivateKeyA, transferContract, 0, 10.Ether(), 1_000_000, TestItem.AddressF.Bytes.PadLeft(32));

        // tx1: Simple transfer from F (initially unfunded, gets balance from tx0)
        Transaction transferFromF = Tx(TestItem.PrivateKeyF, TestItem.AddressB, 0, 5.Ether());

        await using DualBlockchain chains = await DualBlockchain.Create();
        await chains.AddBlock(deployTx);

        (BlockPair blocks, ReceiptPair receipts) =
            chains.ProcessBlockDirectWithReceiptsOnHead([Delay(callContractTx, LongDelayMs)], callContractTx, transferFromF);
        blocks.AssertFullMatch(2);

        // Both should succeed with 2 transactions
        receipts.AssertSuccessful(2);
        // Second tx depends on the balance credited by the delayed first tx.
        AssertLastBlockMetrics(ExpectedMetrics.Create(2, reexecutions: 1, blockedReads: 0, revalidations: 1));
    }
}
