// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Autofac;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.Consensus.Test;

[TestFixtureSource(nameof(Forks))]
public class PrewarmerHandoffTests(IReleaseSpec spec) : PrewarmerHandoffTestBase(spec)
{
    // Shanghai still deletes a self-destructed account; Cancun only does for one created in the same transaction.
    private static readonly IReleaseSpec[] Forks = [Osaka.Instance, Shanghai.Instance];

    [Test]
    public void A_block_of_mixed_transactions_ends_in_the_state_and_receipts_of_executing_it()
    {
        (int replayed, int rejected, _) = Handoff(BuildBlock(
            Call(TestItem.PrivateKeyA, 0, Counter),
            Call(TestItem.PrivateKeyB, 0, Counter),
            Transfer(TestItem.PrivateKeyA, 1, TestItem.AddressC, 1.Wei),
            Transfer(TestItem.PrivateKeyB, 1, TestItem.AddressC, 5.Wei),
            Call(TestItem.PrivateKeyA, 2, Reverter),
            Call(TestItem.PrivateKeyB, 2, Logger),
            Create(TestItem.PrivateKeyC, 0, DeployCode),
            Call(TestItem.PrivateKeyD, 0, BalanceReader)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(replayed, Is.GreaterThan(0));
            Assert.That(rejected, Is.GreaterThan(0));
        }
    }

    [Test]
    public void Payments_to_one_account_replay_while_a_read_of_its_balance_does_not()
    {
        (int replayed, int rejected, _) = Handoff(BuildBlock(
            Transfer(TestItem.PrivateKeyA, 0, TestItem.AddressC, 1.Wei),
            Transfer(TestItem.PrivateKeyB, 0, TestItem.AddressC, 2.Wei),
            Transfer(TestItem.PrivateKeyD, 0, TestItem.AddressC, 3.Wei),
            Call(TestItem.PrivateKeyA, 1, BalanceReader)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(replayed, Is.EqualTo(3));
            Assert.That(rejected, Is.EqualTo(1));
        }
    }

    [Test]
    public void A_senders_later_transaction_replays_after_its_earlier_one_was_executed()
    {
        (int replayed, int rejected, _) = Handoff(BuildBlock(
            Call(TestItem.PrivateKeyA, 0, Counter),
            Call(TestItem.PrivateKeyB, 0, Counter),
            Transfer(TestItem.PrivateKeyB, 1, TestItem.AddressC, 1.Wei)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(replayed, Is.EqualTo(2));
            Assert.That(rejected, Is.EqualTo(1));
        }
    }

    [Test]
    public void A_heavy_senders_transactions_warmed_apart_replay()
    {
        (int replayed, _, _) = Handoff(BuildBlock(
            Transfer(TestItem.PrivateKeyC, 0, TestItem.AddressA, 1.Wei, gasLimit: 2_000_000),
            Transfer(TestItem.PrivateKeyC, 1, TestItem.AddressB, 1.Wei, gasLimit: 2_000_000),
            Transfer(TestItem.PrivateKeyC, 2, TestItem.AddressD, 1.Wei, gasLimit: 2_000_000)));

        Assert.That(replayed, Is.EqualTo(3));
    }

    [Test]
    public void Value_sent_by_a_contract_whose_balance_the_block_changed_replays_while_it_can_pay()
    {
        (int replayed, _, _) = Handoff(BuildBlock(
            Call(TestItem.PrivateKeyA, 0, Payer),
            Call(TestItem.PrivateKeyB, 0, Payer),
            Call(TestItem.PrivateKeyD, 0, Payer)));

        Assert.That(replayed, Is.EqualTo(3));
    }

    [Test]
    public void A_sender_funded_earlier_in_the_block_is_executed()
    {
        PrivateKey unfunded = TestItem.PrivateKeyE;
        (_, _, int missing) = Handoff(BuildBlock(
            Transfer(TestItem.PrivateKeyA, 0, unfunded.Address, 1.Ether),
            Transfer(TestItem.PrivateKeyB, 0, TestItem.AddressC, 1.Wei),
            Transfer(unfunded, 0, TestItem.AddressD, 1.Wei)));

        Assert.That(missing, Is.GreaterThan(0));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void A_destroyed_contract_with_storage_ends_in_the_state_of_executing_it(bool redeployed)
    {
        (int replayed, _, _) = Handoff(BuildBlock(
            Call(TestItem.PrivateKeyA, 0, Child),
            redeployed ? Call(TestItem.PrivateKeyB, 0, Factory, gasLimit: 300_000) : Call(TestItem.PrivateKeyB, 0, Counter),
            Call(TestItem.PrivateKeyD, 0, Child, data: [1])));

        Assert.That(replayed, Is.EqualTo(Spec.IsEip6780Enabled ? 3 : redeployed ? 1 : 2));
    }

    [Test]
    public void A_contract_destroyed_by_a_replay_has_none_of_its_storage_when_deployed_again_later()
    {
        Hash256 executed = DestroyThenRedeploy(handoff: false);
        TearDown();
        Setup();
        Hash256 replayed = DestroyThenRedeploy(handoff: true);

        Assert.That(replayed, Is.EqualTo(executed));
    }

    [Test]
    public void Contracts_created_and_destroyed_within_their_transactions_end_in_the_state_of_executing_them()
    {
        (int replayed, _, _) = Handoff(BuildBlock(
            Call(TestItem.PrivateKeyA, 0, FreshFactory, gasLimit: 300_000, data: [1]),
            Call(TestItem.PrivateKeyB, 0, FreshFactory, gasLimit: 300_000, data: [1]),
            Call(TestItem.PrivateKeyD, 0, FreshFactory, gasLimit: 300_000)));

        // The later deployments read the factory's nonce, which the first one increments.
        Assert.That(replayed, Is.EqualTo(1));
    }

    private Hash256 DestroyThenRedeploy(bool handoff)
    {
        Block destroy = BuildBlock(
            Call(TestItem.PrivateKeyA, 0, Child),
            Call(TestItem.PrivateKeyB, 0, Counter),
            Call(TestItem.PrivateKeyD, 0, Logger));
        Hash256 root;
        if (handoff)
        {
            using BlockCachePreWarmer preWarmer = CreatePreWarmer();
            RunPreWarmCaches(preWarmer, destroy);
            (root, _, (int replayed, _, _)) = Process(destroy, preWarmer);
            Assert.That(replayed, Is.EqualTo(3));
        }
        else
        {
            (root, _, _) = Process(destroy, prewarmer: null);
        }

        BlockHeader first = Processed(destroy, root);
        Block redeploy = BuildBlock(first, Call(TestItem.PrivateKeyB, 1, Factory, gasLimit: 300_000));
        (root, _, _) = Process(redeploy, prewarmer: null, first);
        BlockHeader second = Processed(redeploy, root);
        (root, _, _) = Process(BuildBlock(second, Call(TestItem.PrivateKeyD, 1, Child, data: [1])), prewarmer: null, second);
        return root;
    }
}

[TestFixture]
public class PrewarmerHandoffMechanicsTests() : PrewarmerHandoffTestBase(Osaka.Instance)
{
    [Test]
    public void Footprints_of_another_blocks_transactions_are_not_replayed()
    {
        Block block = BuildBlock(
            Call(TestItem.PrivateKeyA, 0, Counter),
            Transfer(TestItem.PrivateKeyB, 0, TestItem.AddressC, 1.Wei),
            Transfer(TestItem.PrivateKeyC, 0, TestItem.AddressD, 1.Wei));
        Block other = BuildBlock(
            Call(TestItem.PrivateKeyA, 0, Counter),
            Transfer(TestItem.PrivateKeyB, 0, TestItem.AddressC, 1.Wei),
            Transfer(TestItem.PrivateKeyC, 0, TestItem.AddressD, 1.Wei));

        using BlockCachePreWarmer preWarmer = CreatePreWarmer();
        RunPreWarmCaches(preWarmer, other);
        (_, _, (int replayed, _, _)) = Process(block, preWarmer);

        Assert.That(replayed, Is.Zero);
    }

    [Test]
    public void The_recorder_answers_reads_the_interface_derives_from_a_whole_account()
    {
        InterfaceMapping map = typeof(FootprintRecorder).GetInterfaceMap(typeof(IAccountStateProvider));
        int hasCode = Array.FindIndex(map.InterfaceMethods, m => m.Name == nameof(IAccountStateProvider.HasCode));
        Assert.That(map.TargetMethods[hasCode].DeclaringType, Is.EqualTo(typeof(FootprintRecorder)));
    }

    [Test]
    public void A_run_stops_once_block_processing_starts_its_transaction_and_is_undone()
    {
        IWorldState worldState = ProcessingScope.Resolve<IWorldState>();
        using (worldState.BeginScope(Parent))
        {
            FootprintRecorder recorder = new(worldState);
            Progress progress = new() { MainThreadTxIndex = 1 };
            UInt256 before = worldState.GetBalance(TestItem.AddressC);

            recorder.Start(progress, txIndex: 2, CancellationToken.None);
            recorder.AddToBalance(TestItem.AddressC, 0x4e4d, Spec, out _);
            ITxTracer outcome = recorder.Outcome;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(outcome.IsCancelable, Is.True);
                Assert.That(outcome.IsCancelled, Is.False);
                progress.MainThreadTxIndex = 2;
                Assert.That(outcome.IsCancelled, Is.True);
            }

            recorder.Discard();
            Assert.That(worldState.GetBalance(TestItem.AddressC), Is.EqualTo(before));
        }
    }

    private sealed class Progress : IBlockProcessingProgress
    {
        public int MainThreadTxIndex { get; set; }
    }
}

public abstract class PrewarmerHandoffTestBase(IReleaseSpec spec)
{
    protected IReleaseSpec Spec { get; } = spec;

    // PUSH0 SLOAD PUSH1 1 ADD PUSH0 SSTORE STOP
    private static readonly byte[] CounterCode = [0x5F, 0x54, 0x60, 0x01, 0x01, 0x5F, 0x55, 0x00];
    // PUSH0 PUSH0 REVERT
    private static readonly byte[] RevertCode = [0x5F, 0x5F, 0xFD];
    // MSTORE(0, 0x4e4d); LOG1(0, 32, 1); STOP
    private static readonly byte[] LogCode = [0x61, 0x4E, 0x4D, 0x5F, 0x52, 0x60, 0x01, 0x60, 0x20, 0x5F, 0xA1, 0x00];
    // Deploys the runtime code 0xFE.
    protected static readonly byte[] DeployCode = [0x60, 0xFE, 0x5F, 0x53, 0x60, 0x01, 0x5F, 0xF3];
    // CALL(GAS, CALLER, 0x4e4d, 0, 0, 0, 0); POP; STOP
    private static readonly byte[] PayerCode = [0x5F, 0x5F, 0x5F, 0x5F, 0x61, 0x4E, 0x4D, 0x33, 0x5A, 0xF1, 0x50, 0x00];
    // Without call data SELFDESTRUCT(CALLER), with it SSTORE(0x4e4d, SLOAD(0x4e65746865726d696e64)).
    private static readonly byte[] ChildCode =
        [0x36, 0x60, 0x06, 0x57, 0x33, 0xFF, 0x5B, 0x69, 0x4E, 0x65, 0x74, 0x68, 0x65, 0x72, 0x6D, 0x69, 0x6E, 0x64, 0x54, 0x61, 0x4E, 0x4D, 0x55, 0x00];
    // SSTORE(0, 0x4e4d); SSTORE(1, 2); returns the child code.
    private static readonly byte[] ChildInitCode =
        [0x61, 0x4E, 0x4D, 0x5F, 0x55, 0x60, 0x02, 0x60, 0x01, 0x55, 0x60, 0x18, 0x60, 0x14, 0x5F, 0x39, 0x60, 0x18, 0x5F, 0xF3, .. ChildCode];
    // CREATE2 of the child init code appended to it; with call data, then calls the child.
    private static readonly byte[] FactoryCode =
        [0x60, 0x2C, 0x60, 0x1D, 0x5F, 0x39, 0x61, 0x4E, 0x4D, 0x60, 0x2C, 0x5F, 0x5F, 0xF5, 0x36, 0x60, 0x13, 0x57, 0x00,
         0x5B, 0x5F, 0x5F, 0x5F, 0x5F, 0x5F, 0x85, 0x5A, 0xF1, 0x00, .. ChildInitCode];

    private static readonly byte[] Salt = [.. new byte[30], 0x4E, 0x4D];
    private static readonly UInt256 ChildSlot = new(0x746865726d696e64UL, 0x4e65UL, 0, 0);

    protected static readonly Address Counter = new("0x000000000000000000004e65746865726d696e64");
    protected static readonly Address Reverter = new("0x00000000000000000000000000000000004e4d01");
    protected static readonly Address Logger = new("0x00000000000000000000000000000000004e4d02");
    protected static readonly Address BalanceReader = new("0x00000000000000000000000000000000004e4d03");
    protected static readonly Address Payer = new("0x00000000000000000000000000000000004e4d04");
    protected static readonly Address Factory = new("0x00000000000000000000000000000000004e4d05");
    protected static readonly Address FreshFactory = new("0x00000000000000000000000000000000004e4d06");
    protected static readonly Address Child = ContractAddress.From(Factory, Salt, ChildInitCode);

    private IContainer _container = null!;
    private readonly List<BlockHeader> _headers = [];

    protected ILifetimeScope ProcessingScope { get; private set; } = null!;
    protected BlockHeader Parent { get; private set; } = null!;

    [SetUp]
    public void Setup()
    {
        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(Spec))
            .AddSingleton<IStateHeaderProvider>(new Parents(_headers))
            .Build();

        IMainProcessingModule[] mainModules = _container.Resolve<IMainProcessingModule[]>();
        IWorldStateManager worldStateManager = _container.Resolve<IWorldStateManager>();
        IWorldStateScopeProvider scopeProvider = worldStateManager.GlobalWorldState;
        ProcessingScope = _container.BeginLifetimeScope(b =>
        {
            b.RegisterInstance(scopeProvider).As<IWorldStateScopeProvider>().ExternallyOwned();
            b.RegisterInstance(worldStateManager).As<IWorldStateManager>().ExternallyOwned();
            b.AddModule(mainModules);
        });

        IWorldState worldState = ProcessingScope.Resolve<IWorldState>();
        Hash256 genesisRoot;
        using (worldState.BeginScope(IWorldState.PreGenesis))
        {
            worldState.CreateAccount(TestItem.AddressA, 1_000.Ether);
            worldState.CreateAccount(TestItem.AddressB, 1_000.Ether);
            worldState.CreateAccount(TestItem.AddressC, 1_000.Ether);
            worldState.CreateAccount(TestItem.AddressD, 1_000.Ether);
            Deploy(worldState, Counter, CounterCode, 0);
            Deploy(worldState, Reverter, RevertCode, 0);
            Deploy(worldState, Logger, LogCode, 0);
            Deploy(worldState, Payer, PayerCode, 1.Ether);
            // PUSH20 C BALANCE PUSH0 SSTORE STOP
            Deploy(worldState, BalanceReader, [0x73, .. TestItem.AddressC.Bytes, 0x31, 0x5F, 0x55, 0x00], 0);
            Deploy(worldState, Factory, FactoryCode, 0);
            Deploy(worldState, FreshFactory, FactoryCode, 0);
            Deploy(worldState, Child, ChildCode, 0x4e4d);
            worldState.Set(new StorageCell(Child, 0), 0x4e4d);
            worldState.Set(new StorageCell(Child, 1), 2);
            worldState.Set(new StorageCell(Child, ChildSlot), 0x4e4d);
            worldState.Commit(Spec);
            worldState.CommitTree(0);
            genesisRoot = worldState.StateRoot;
        }

        Parent = Build.A.BlockHeader
            .WithNumber(0)
            .WithStateRoot(genesisRoot)
            .WithGasLimit(30_000_000)
            .WithHash(Build.A.BlockHeader.TestObject.ParentHash!)
            .TestObject;
        _headers.Clear();
        _headers.Add(Parent);

        void Deploy(IWorldState state, Address address, byte[] code, UInt256 balance)
        {
            state.CreateAccount(address, balance);
            state.InsertCode(address, Keccak.Compute(code), code, Spec);
        }
    }

    [TearDown]
    public void TearDown()
    {
        ProcessingScope?.Dispose();
        _container?.Dispose();
    }

    /// <summary>Processes the block by execution, then warmed and handed off, and requires the same root and receipts.</summary>
    protected (int Replayed, int Rejected, int Missing) Handoff(Block block)
    {
        (Hash256 executedRoot, TxReceipt[] executed, _) = Process(block, prewarmer: null);
        using BlockCachePreWarmer preWarmer = CreatePreWarmer();
        RunPreWarmCaches(preWarmer, block);
        (Hash256 root, TxReceipt[] receipts, (int Replayed, int Rejected, int Missing) tally) = Process(block, preWarmer);

        Assert.That(root, Is.EqualTo(executedRoot));
        AssertSameReceipts(receipts, executed);
        return tally;
    }

    protected BlockCachePreWarmer CreatePreWarmer()
    {
        PrewarmerEnvFactory envFactory = ProcessingScope.Resolve<PrewarmerEnvFactory>();
        Assert.That(envFactory.RecordsFootprints, Is.True);
        PreBlockCaches preBlockCaches = ProcessingScope.Resolve<PreBlockCaches>();
        return new BlockCachePreWarmer(
            new BlockCachePreWarmer.ReadOnlyTxProcessingEnvPooledObjectPolicy(envFactory, preBlockCaches),
            minPoolSize: 4,
            concurrency: 3,
            parallelExecutionBatchRead: true,
            ProcessingScope.Resolve<NodeStorageCache>(),
            preBlockCaches,
            LimboLogs.Instance,
            handoff: true);
    }

    // Sync on purpose: the scope is closed on the thread that opened it.
    protected void RunPreWarmCaches(BlockCachePreWarmer preWarmer, Block block)
    {
        IWorldState worldState = ProcessingScope.Resolve<IWorldState>();
        using (worldState.BeginScope(Parent))
        {
            using IDisposable? session = preWarmer.PreWarmCaches(block, Parent, Spec);
            ((PrewarmingSession?)session)?.WaitForCompletion();
        }
    }

    protected (Hash256 StateRoot, TxReceipt[] Receipts, (int Replayed, int Rejected, int Missing) Tally) Process(Block block, BlockCachePreWarmer? prewarmer, BlockHeader? parent = null)
    {
        IWorldState worldState = ProcessingScope.Resolve<IWorldState>();
        ITransactionProcessor processor = ProcessingScope.Resolve<ITransactionProcessor>();
        Block processing = new(block.Header.CloneForProcessing(), block.Body);
        using (worldState.BeginScope(parent ?? Parent))
        {
            ITransactionProcessorAdapter executor = new ExecuteTransactionProcessorAdapter(processor);
            PrewarmerTxAdapter? adapter = prewarmer is null ? null
                : new PrewarmerTxAdapter(executor, prewarmer, new PrewarmerState(ProcessingScope.Resolve<PreBlockCaches>(), isPrewarmer: false), worldState);
            ITransactionProcessorAdapter transactions = adapter ?? executor;
            BlockReceiptsTracer tracer = new();
            tracer.SetOtherTracer(NullBlockTracer.Instance);
            tracer.StartNewBlockTrace(processing);
            transactions.SetBlockExecutionContext(new BlockExecutionContext(processing.Header, Spec));
            foreach (Transaction tx in processing.Transactions)
            {
                using ITxTracer txTracer = tracer.StartNewTxTrace(tx);
                TransactionResult result = transactions.Execute(tx, tracer);
                tracer.EndTxTrace();
                Assert.That((bool)result, Is.True, $"transaction {tx.Hash} must be valid: {result}");
            }

            worldState.Commit(Spec);
            worldState.CommitTree(block.Number);
            return (worldState.StateRoot, [.. tracer.TxReceipts], adapter?.Tally ?? default);
        }
    }

    private static void AssertSameReceipts(TxReceipt[] actual, TxReceipt[] expected)
    {
        Assert.That(actual.Length, Is.EqualTo(expected.Length));
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.That(actual[i].StatusCode, Is.EqualTo(expected[i].StatusCode), $"status of {i}");
            Assert.That(actual[i].GasUsed, Is.EqualTo(expected[i].GasUsed), $"gas of {i}");
            Assert.That(actual[i].GasUsedTotal, Is.EqualTo(expected[i].GasUsedTotal), $"cumulative gas of {i}");
            Assert.That(actual[i].ContractAddress, Is.EqualTo(expected[i].ContractAddress), $"contract address of {i}");
            Assert.That(actual[i].Recipient, Is.EqualTo(expected[i].Recipient), $"recipient of {i}");
            Assert.That(actual[i].Logs!.Length, Is.EqualTo(expected[i].Logs!.Length), $"log count of {i}");
            for (int j = 0; j < expected[i].Logs!.Length; j++)
            {
                Assert.That(actual[i].Logs![j].Address, Is.EqualTo(expected[i].Logs![j].Address), $"log {j} address of {i}");
                Assert.That(actual[i].Logs![j].Data, Is.EqualTo(expected[i].Logs![j].Data), $"log {j} data of {i}");
                Assert.That(actual[i].Logs![j].Topics, Is.EqualTo(expected[i].Logs![j].Topics), $"log {j} topics of {i}");
            }
        }
    }

    protected Block BuildBlock(params Transaction[] transactions) => BuildBlock(Parent, transactions);

    protected static Block BuildBlock(BlockHeader parent, params Transaction[] transactions) =>
        Build.A.Block.WithNumber(parent.Number + 1)
            .WithParent(parent)
            .WithBeneficiary(TestItem.AddressF)
            .WithBaseFeePerGas(1.GWei)
            .WithTimestamp(parent.Timestamp + 12)
            .WithTransactions(transactions)
            .WithGasLimit(30_000_000)
            .TestObject;

    /// <summary>The header of <paramref name="block"/> processed into <paramref name="stateRoot"/>.</summary>
    protected BlockHeader Processed(Block block, Hash256 stateRoot)
    {
        BlockHeader header = block.Header.Clone();
        header.StateRoot = stateRoot;
        header.Hash = header.CalculateHash();
        _headers.Add(header);
        return header;
    }

    protected static Transaction Call(PrivateKey sender, ulong nonce, Address to, ulong gasLimit = 100_000, byte[]? data = null) =>
        Build.A.Transaction.WithType(TxType.EIP1559).WithNonce(nonce).WithTo(to).WithValue(UInt256.Zero).WithGasLimit(gasLimit).WithData(data ?? [])
            .WithMaxFeePerGas(2.GWei).WithMaxPriorityFeePerGas(1.GWei)
            .SignedAndResolved(sender).TestObject;

    protected static Transaction Transfer(PrivateKey sender, ulong nonce, Address to, UInt256 value, ulong gasLimit = GasCostOf.Transaction) =>
        Build.A.Transaction.WithType(TxType.EIP1559).WithNonce(nonce).WithTo(to).WithValue(value).WithGasLimit(gasLimit)
            .WithMaxFeePerGas(2.GWei).WithMaxPriorityFeePerGas(1.GWei)
            .SignedAndResolved(sender).TestObject;

    protected static Transaction Create(PrivateKey sender, ulong nonce, byte[] initCode) =>
        Build.A.Transaction.WithType(TxType.EIP1559).WithNonce(nonce).WithCode(initCode).WithGasLimit(200_000)
            .WithMaxFeePerGas(2.GWei).WithMaxPriorityFeePerGas(1.GWei)
            .SignedAndResolved(sender).TestObject;

    private sealed class Parents(List<BlockHeader> headers) : IStateHeaderProvider
    {
        public BlockHeader? FindParentHeader(BlockHeader target) => headers.Find(header => header.Hash == target.ParentHash);
        public ulong FinalizedBlockNumber => 0;
        public BlockHeader? GetFinalizedHeader(ulong blockNumber) => null;
    }
}
