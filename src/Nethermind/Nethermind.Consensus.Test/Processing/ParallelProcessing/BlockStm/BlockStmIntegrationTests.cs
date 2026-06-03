// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Processing.ParallelProcessing.BlockStm;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;
using StmMetrics = Nethermind.Consensus.Processing.ParallelProcessing.BlockStm.Metrics;

namespace Nethermind.Consensus.Test.Processing.ParallelProcessing.BlockStm;

/// <summary>
/// Integration tests that run each scenario on both a STM-enabled and a sequential
/// blockchain and assert state-root parity. Covers selfdestruct edge cases (audit B4,
/// EIP-6780, beneficiary==self) plus basic independence.
/// </summary>
[NonParallelizable]
public class BlockStmIntegrationTests
{
    private const long TestBlockGasLimit = 30_000_000;
    private const int LongDelayMs = 200;
    private static readonly TxDelay[] NoDelays = [];

    [SetUp]
    public void Setup() => StmMetrics.ResetForTests();

    public readonly record struct TxDelay(Transaction Transaction, int Milliseconds);
    private static TxDelay Delay(Transaction transaction, int milliseconds) => new(transaction, milliseconds);

    private sealed class TransactionDelayPolicy
    {
        private readonly Dictionary<Hash256, int> _delays = [];

        public void SetDelays(TxDelay[] delays)
        {
            _delays.Clear();
            foreach (TxDelay d in delays)
            {
                if (d.Milliseconds > 0 && d.Transaction.Hash is not null)
                {
                    _delays[d.Transaction.Hash] = d.Milliseconds;
                }
            }
        }

        public bool TryGetDelay(Transaction tx, out int ms)
        {
            ms = 0;
            return tx.Hash is not null && _delays.TryGetValue(tx.Hash, out ms);
        }
    }

    private sealed class DelayedTransactionProcessor(ITransactionProcessor inner, TransactionDelayPolicy policy)
        : ITransactionProcessor
    {
        public TransactionResult Process(Transaction tx, ITxTracer t, ExecutionOptions opts) { Apply(tx); return inner.Process(tx, t, opts); }
        public void SetBlockExecutionContext(BlockHeader h) => inner.SetBlockExecutionContext(h);
        public void SetBlockExecutionContext(in BlockExecutionContext c) => inner.SetBlockExecutionContext(in c);
        private void Apply(Transaction tx) { if (policy.TryGetDelay(tx, out int ms) && ms > 0) Thread.Sleep(ms); }
    }

    private sealed class GenesisGasLimitOverride(long gasLimit) : IGenesisPostProcessor
    {
        public void PostProcess(Block genesis) => genesis.Header.GasLimit = gasLimit;
    }

    private class StmTestBlockchain(IBlocksConfig blocksConfig, IReleaseSpec releaseSpec) : TestBlockchain
    {
        public TransactionDelayPolicy DelayPolicy { get; } = new();

        public static async Task<StmTestBlockchain> Create(IBlocksConfig blocksConfig, IReleaseSpec? releaseSpec = null) =>
            (StmTestBlockchain)await new StmTestBlockchain(blocksConfig, releaseSpec ?? Cancun.Instance).Build();

        protected override Task AddBlocksOnStart() => Task.CompletedTask;

        protected override IEnumerable<IConfig> CreateConfigs() => [blocksConfig];

        protected override ContainerBuilder ConfigureContainer(ContainerBuilder builder, IConfigProvider configProvider) =>
            base.ConfigureContainer(builder, configProvider)
                .AddSingleton<ISpecProvider>(new TestSpecProvider(releaseSpec))
                .AddSingleton<IGenesisPostProcessor>(new GenesisGasLimitOverride(TestBlockGasLimit))
                .AddSingleton(DelayPolicy)
                .AddDecorator<ITransactionProcessor, DelayedTransactionProcessor>();
    }

    private sealed class DualBlockchain(StmTestBlockchain parallel, StmTestBlockchain single) : IAsyncDisposable
    {
        private StmTestBlockchain Parallel { get; } = parallel;
        private StmTestBlockchain Single { get; } = single;

        public static async Task<DualBlockchain> Create(IReleaseSpec? releaseSpec = null) =>
            new(await StmTestBlockchain.Create(BuildConfig(true), releaseSpec),
                await StmTestBlockchain.Create(BuildConfig(false), releaseSpec));

        // Direct ProcessOne, bypassing txpool / block builder. Codex used the same pattern
        // for unit-style tests because AddBlock-via-pool surfaces too many unrelated failures
        // (insufficient balance, gas-pricing, pool ordering) that aren't what we're testing.
        public BlockPair ProcessBlock(params Transaction[] transactions) => ProcessBlock(NoDelays, transactions);

        public BlockPair ProcessBlock(TxDelay[] delays, params Transaction[] transactions)
        {
            Parallel.DelayPolicy.SetDelays(delays);
            return new(ProcessDirect(Parallel, transactions), ProcessDirect(Single, transactions));
        }

        private static Block ProcessDirect(StmTestBlockchain chain, Transaction[] transactions)
        {
            BlockHeader parent = chain.BlockTree.Head!.Header;
            Block block = Build.A.Block
                .WithParent(parent)
                .WithGasLimit(TestBlockGasLimit)
                .WithTransactions(transactions)
                .TestObject;
            IReleaseSpec spec = chain.SpecProvider.GetSpec(block.Header);
            using IDisposable scope = chain.MainProcessingContext.WorldState.BeginScope(parent);
            return chain.MainProcessingContext.BlockProcessor
                .ProcessOne(block, ProcessingOptions.NoValidation, NullBlockTracer.Instance, spec, CancellationToken.None).Block;
        }

        public ValueTask DisposeAsync()
        {
            Parallel.Dispose();
            Single.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private readonly record struct BlockPair(Block Parallel, Block Single)
    {
        public void AssertFullMatch(int expectedTxCount)
        {
            Block p = Parallel, s = Single;
            Assert.Multiple(() =>
            {
                Assert.That(p.Transactions, Has.Length.EqualTo(expectedTxCount));
                Assert.That(s.Transactions, Has.Length.EqualTo(expectedTxCount));
                Assert.That(p.Header.GasUsed, Is.EqualTo(s.Header.GasUsed));
                Assert.That(p.Header.StateRoot, Is.EqualTo(s.Header.StateRoot), "state root must match sequential baseline");
            });
        }
    }

    private static IBlocksConfig BuildConfig(bool stm) =>
        new BlocksConfig
        {
            MinGasPrice = 0,
            // Prewarmer is independent of STM; both can be on for a parallel run.
            PreWarmStateOnBlockProcessing = true,
            BlockStmEnabled = stm
        };

    // ─── transaction helpers ──────────────────────────────────────────────────────

    private static Transaction Tx(PrivateKey from, Address to, UInt256 nonce, UInt256? value = null,
        long gasLimit = 1_000_000, byte[]? data = null) =>
        Build.A.Transaction
            .WithType(TxType.EIP1559)
            .To(to)
            .WithNonce(nonce)
            .WithChainId(BlockchainIds.Mainnet)
            .WithMaxFeePerGas(2.GWei)
            .WithMaxPriorityFeePerGas(1.GWei)
            .WithValue(value ?? 1.Ether)
            .WithGasLimit(gasLimit)
            .WithData(data ?? [])
            .SignedAndResolved(from, false)
            .TestObject;

    private static Transaction TxToContract(PrivateKey from, Address to, UInt256 nonce, byte[] data,
        UInt256? value = null, long gasLimit = 200_000) =>
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

    // ─── tests ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Independent_transfers_match_sequential()
    {
        await using DualBlockchain chains = await DualBlockchain.Create();

        BlockPair blocks = chains.ProcessBlock(
            Tx(TestItem.PrivateKeyA, TestItem.AddressD, 0, 1.Ether),
            Tx(TestItem.PrivateKeyB, TestItem.AddressE, 0, 1.Ether),
            Tx(TestItem.PrivateKeyC, TestItem.AddressF, 0, 1.Ether)
        );

        blocks.AssertFullMatch(3);
    }

    [Test]
    public async Task Nonce_chain_from_one_sender_matches_sequential()
    {
        await using DualBlockchain chains = await DualBlockchain.Create();

        BlockPair blocks = chains.ProcessBlock(
            Tx(TestItem.PrivateKeyA, TestItem.AddressD, 0, 1.Ether),
            Tx(TestItem.PrivateKeyA, TestItem.AddressE, 1, 1.Ether),
            Tx(TestItem.PrivateKeyA, TestItem.AddressF, 2, 1.Ether)
        );

        blocks.AssertFullMatch(3);
    }

    [Test]
    public async Task SelfDestruct_recreate_in_same_transaction_matches_sequential()
    {
        // Shanghai: SELFDESTRUCT does full destroy + recreate within one outer tx.
        // The CREATE2 → CALL(selfdestruct) → CREATE2 pattern from the codex audit suite.
        await using DualBlockchain chains = await DualBlockchain.Create(Shanghai.Instance);

        byte[] selfDestructCode = Prepare.EvmCode.SELFDESTRUCT(TestItem.AddressB).Done;
        byte[] initCode = Prepare.EvmCode.ForInitOf(selfDestructCode).Done;

        byte[] salt = new UInt256(123).ToBigEndian();
        Address createAddress = ContractAddress.From(TestItem.AddressA, 0);
        Address contractAddress = ContractAddress.From(createAddress, salt, initCode);

        byte[] create2Code = Prepare.EvmCode
            .Create2(initCode, salt, 1.Ether)
            .Call(contractAddress, 50_000)
            .Create2(initCode, salt, 1.Ether)
            .STOP()
            .Done;
        byte[] create2InitCode = Prepare.EvmCode.ForInitOf(create2Code).Done;

        BlockPair blocks = chains.ProcessBlock(TxCreateContract(TestItem.PrivateKeyA, 0, create2InitCode, 10.Ether));

        blocks.AssertFullMatch(1);
    }

    [Test]
    public async Task SelfDestruct_with_beneficiary_equal_to_self_matches_sequential()
    {
        // Beneficiary == self → balance is burnt (Shanghai+) or transferred to self (older).
        // Tests that the parallel scope doesn't introduce spurious self-references in either path.
        await using DualBlockchain chains = await DualBlockchain.Create(Shanghai.Instance);

        // Contract code: SELFDESTRUCT(ADDRESS) → beneficiary is the contract itself.
        byte[] selfDestructToSelf = Prepare.EvmCode
            .Op(Instruction.ADDRESS)
            .Op(Instruction.SELFDESTRUCT)
            .Done;
        byte[] initCode = Prepare.EvmCode.ForInitOf(selfDestructToSelf).Done;

        Address contractAddress = ContractAddress.From(TestItem.AddressA, 0);

        BlockPair blocks = chains.ProcessBlock(
            TxCreateContract(TestItem.PrivateKeyA, 0, initCode, 5.Ether),
            TxToContract(TestItem.PrivateKeyB, contractAddress, 0, [])
        );

        blocks.AssertFullMatch(2);
    }

    [Test]
    public async Task SelfDestruct_post_Cancun_keeps_contract_unless_created_same_tx()
    {
        // EIP-6780: SELFDESTRUCT in Cancun only destroys when called in the same tx as CREATE.
        // For a contract created in a separate tx, SELFDESTRUCT only transfers the balance —
        // code and storage persist. Tests state-root parity across that edge.
        await using DualBlockchain chains = await DualBlockchain.Create(Cancun.Instance);

        // Deploy + fund + selfdestruct + read (state must show contract still exists with empty balance).
        byte[] selfDestructCode = Prepare.EvmCode.SELFDESTRUCT(TestItem.AddressB).Done;
        byte[] initCode = Prepare.EvmCode.ForInitOf(selfDestructCode).Done;

        Address contractAddress = ContractAddress.From(TestItem.AddressA, 0);

        BlockPair blocks = chains.ProcessBlock(
            // tx0: deploy with 5 ether
            TxCreateContract(TestItem.PrivateKeyA, 0, initCode, 5.Ether),
            // tx1: trigger selfdestruct (EIP-6780: balance moves, contract stays)
            TxToContract(TestItem.PrivateKeyB, contractAddress, 0, [])
        );

        blocks.AssertFullMatch(2);
    }

    [Test]
    public async Task Store_then_destroy_then_send_three_txs_matches_sequential()
    {
        // Bug-4 regression scenario: tx0 stores something to a contract; tx1 selfdestructs
        // the contract (account → null); tx2 sends ETH to the same address (re-creates a
        // plain account). PushChanges previously misclassified tx2's non-null account
        // update as a delete, wiping tx0's stored storage. The fix here is the
        // accountDeletedToNull check (require value == null, not just presence).
        await using DualBlockchain chains = await DualBlockchain.Create(Shanghai.Instance);

        // Storage-write helper: SSTORE(slot=0, value=42); SELFDESTRUCT-on-input-flag.
        // calldata[0] == 1 → SELFDESTRUCT(AddressF); else → SSTORE(0, 42).
        byte[] code = Prepare.EvmCode
            .Op(Instruction.PUSH0)
            .Op(Instruction.CALLDATALOAD)
            .Op(Instruction.ISZERO)
            .PushData(20)
            .Op(Instruction.JUMPI)
            // selfdestruct branch
            .PushData(TestItem.AddressF)
            .Op(Instruction.SELFDESTRUCT)
            // store branch (JUMPDEST at offset 20)
            .Op(Instruction.JUMPDEST)
            .PushData(42)
            .Op(Instruction.PUSH0)
            .Op(Instruction.SSTORE)
            .STOP()
            .Done;
        byte[] initCode = Prepare.EvmCode.ForInitOf(code).Done;

        Address contractAddress = ContractAddress.From(TestItem.AddressA, 0);

        BlockPair blocks = chains.ProcessBlock(
            // tx0: deploy
            TxCreateContract(TestItem.PrivateKeyA, 0, initCode, 5.Ether),
            // tx1: trigger selfdestruct
            TxToContract(TestItem.PrivateKeyB, contractAddress, 0, [(byte)1]),
            // tx2: plain ETH transfer to the now-deleted address
            Tx(TestItem.PrivateKeyC, contractAddress, 0, 1.Ether)
        );

        blocks.AssertFullMatch(3);
    }

    [Test]
    public async Task Cross_contract_calls_match_sequential_under_delays()
    {
        // Receiver contract created later than caller's call — STM forces revalidation;
        // parity vs sequential is the only assertion.
        await using DualBlockchain chains = await DualBlockchain.Create(Shanghai.Instance);

        byte[] receiverCode = Prepare.EvmCode
            .Op(Instruction.CALLVALUE)
            .Op(Instruction.PUSH0)
            .Op(Instruction.SSTORE)
            .STOP()
            .Done;
        byte[] receiverInitCode = Prepare.EvmCode.ForInitOf(receiverCode).Done;
        Address receiverAddress = ContractAddress.From(TestItem.AddressA, 0);

        byte[] callerCode = Prepare.EvmCode
            .CallWithValue(receiverAddress, 50_000)
            .STOP()
            .Done;
        byte[] callerInitCode = Prepare.EvmCode.ForInitOf(callerCode).Done;

        Transaction receiverCreate = TxCreateContract(TestItem.PrivateKeyA, 0, receiverInitCode);
        Transaction callerCreate = TxCreateContract(TestItem.PrivateKeyB, 0, callerInitCode, 5.Ether);
        Transaction callerCall = TxToContract(TestItem.PrivateKeyB,
            ContractAddress.From(TestItem.AddressB, 0), 1, [], 2.Ether);

        BlockPair blocks = chains.ProcessBlock(
            [Delay(receiverCreate, LongDelayMs)],
            receiverCreate, callerCreate, callerCall);

        blocks.AssertFullMatch(3);
    }
}
