// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Tracing;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Container;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Container;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State.OverridableEnv;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

public partial class BlockProcessorTests
{
    private static readonly Address SharedContract = TestItem.AddressE;
    private static readonly Address Delegated = TestItem.PrivateKeyD.Address;
    private static readonly Address Beneficiary = TestItem.AddressF;

    // slot0 += 1; slot1 = CALLVALUE (5 -> 0 refunds); TSTORE(2, 7); slot3 = TLOAD(2).
    private static readonly byte[] SharedContractCode =
    [
        0x60, 0x00, 0x54, 0x60, 0x01, 0x01, 0x60, 0x00, 0x55,
        0x34, 0x60, 0x01, 0x55,
        0x60, 0x07, 0x60, 0x02, 0x5d,
        0x60, 0x02, 0x5c, 0x60, 0x03, 0x55,
        0x00,
    ];

    private const string ReadingJavaScriptTracer =
        "{ops: [], step: function(log, db) { var op = log.op.toString(); if (op == 'SLOAD' || op == 'SSTORE' || op == 'TLOAD') this.ops.push(op + ':' + log.getGas()); }," +
        " result: function(ctx, db) { return { ops: this.ops, balance: db.getBalance(ctx.from).toString(), nonce: db.getNonce(ctx.from) }; }, fault: function() {}}";

    public static IEnumerable<TestCaseData> AccessListSeedGethTracers()
    {
        yield return Case(new GethTraceOptions { Tracer = "callTracer" }, "callTracer");
        yield return Case(new GethTraceOptions { Tracer = "callTracer", TracerConfig = Config("{\"withLog\":true}") }, "callTracerWithLogs");
        yield return Case(new GethTraceOptions { Tracer = "prestateTracer" }, "prestateTracer");
        yield return Case(new GethTraceOptions { Tracer = "prestateTracer", TracerConfig = Config("{\"diffMode\":true}") }, "prestateTracerDiffMode");
        yield return Case(new GethTraceOptions { Tracer = "4byteTracer" }, "4byteTracer");
        yield return Case(new GethTraceOptions { Tracer = "stateGasTracer" }, "stateGasTracer");
        yield return Case(new GethTraceOptions { EnableMemory = true, EnableReturnData = true }, "structLogs");
        yield return Case(new GethTraceOptions { Tracer = ReadingJavaScriptTracer }, "javaScriptTracer");

        static TestCaseData Case(GethTraceOptions options, string name) =>
            new TestCaseData(options).SetName($"AccessListSeed_WhenTracingEachTransaction_ExecutesOnlyItAndMatchesTheReplay({name})");

        static JsonElement Config(string json) => JsonDocument.Parse(json).RootElement;
    }

    [TestCaseSource(nameof(AccessListSeedGethTracers))]
    public async Task AccessListSeed_WhenTracingEachTransaction_ExecutesOnlyItAndMatchesTheReplay(GethTraceOptions options)
    {
        // Every transaction is traced alone, once replaying its prefix and once seeded from the block's access list,
        // read back from the store as for any historical block; the seeded trace executes only its target.
        using BasicTestBlockchain chain = await CreateAccessListSeedChain();
        BlockHeader parent = chain.BlockTree.Head!.Header;
        Block block = Historical(await AddSharedStateBlock(chain));
        IPrefixStateSeedSource seeds = chain.Container.Resolve<IPrefixStateSeedSource>();

        for (int i = 0; i < block.Transactions.Length; i++)
        {
            Hash256 target = block.Transactions[i].Hash!;
            GethTraceOptions traceOptions = options with { TxHash = target };
            ExecutionCounter replayed = new();
            ExecutionCounter seeded = new();

            string expected = SerializeGeth(chain, TraceOneThroughTraceEnvironment(chain, parent, block, target, GethTracer(chain, block, traceOptions), seeds: null, replayed));
            string actual = SerializeGeth(chain, TraceOneThroughTraceEnvironment(chain, parent, block, target, GethTracer(chain, block, traceOptions), seeds, seeded));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(replayed.Calls, Is.EqualTo(i + 1), $"precondition: the replay of transaction {i} executes its prefix");
                Assert.That(seeded.Calls, Is.EqualTo(1), $"transaction {i} is seeded from the access list, so only it executes");
                Assert.That(actual, Is.EqualTo(expected), $"the seeded trace of transaction {i} must be byte-identical to the replay");
            }
        }
    }

    [Test]
    public async Task AccessListSeed_WhenTracingEachTransactionParityStyle_ExecutesOnlyItAndMatchesTheReplay()
    {
        using BasicTestBlockchain chain = await CreateAccessListSeedChain();
        BlockHeader parent = chain.BlockTree.Head!.Header;
        Block block = Historical(await AddSharedStateBlock(chain));
        IPrefixStateSeedSource seeds = chain.Container.Resolve<IPrefixStateSeedSource>();
        ParityTraceTypes types = ParityTraceTypes.Trace | ParityTraceTypes.StateDiff | ParityTraceTypes.VmTrace;

        for (int i = 0; i < block.Transactions.Length; i++)
        {
            Hash256 target = block.Transactions[i].Hash!;
            ExecutionCounter replayed = new();
            ExecutionCounter seeded = new();

            string expected = chain.JsonSerializer.Serialize(new ParityTxTraceFromReplay(
                TraceOneThroughTraceEnvironment(chain, parent, block, target, _ => new ParityLikeBlockTracer(target, types), seeds: null, replayed), true));
            string actual = chain.JsonSerializer.Serialize(new ParityTxTraceFromReplay(
                TraceOneThroughTraceEnvironment(chain, parent, block, target, _ => new ParityLikeBlockTracer(target, types), seeds, seeded), true));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(replayed.Calls, Is.EqualTo(i + 1), $"precondition: the replay of transaction {i} executes its prefix");
                Assert.That(seeded.Calls, Is.EqualTo(1), $"transaction {i} is seeded from the access list, so only it executes");
                Assert.That(actual, Is.EqualTo(expected), $"trace, state diff and VM trace of transaction {i} must be byte-identical to the replay");
            }
        }
    }

    [TestCase(false, TestName = "AccessListSeed_WhenTheListIsCarriedByTheBlock_SeedsWithoutTheStore")]
    [TestCase(true, TestName = "AccessListSeed_WhenTheListIsReadFromTheStore_Seeds")]
    public async Task AccessListSeed_WhereverTheValidListComesFrom_Seeds(bool fromStore)
    {
        using BasicTestBlockchain chain = await CreateAccessListSeedChain();
        BlockHeader parent = chain.BlockTree.Head!.Header;
        Block produced = await AddSharedStateBlock(chain);
        IBlockAccessListStore store = chain.Container.Resolve<IBlockAccessListStore>();
        ReadOnlyBlockAccessList stored = store.Get((ulong)produced.Number, produced.Hash!)!;
        Assert.That(stored.WireHash, Is.EqualTo(produced.Header.BlockAccessListHash), "precondition: the store holds the list the header commits to");
        if (!fromStore) store.Delete((ulong)produced.Number, produced.Hash!);
        Block block = fromStore ? Historical(produced) : new Block(produced.Header, produced.Body, stored);
        Hash256 target = block.Transactions[^1].Hash!;
        GethTraceOptions options = new() { TxHash = target, Tracer = "prestateTracer", TracerConfig = JsonDocument.Parse("{\"diffMode\":true}").RootElement };
        ExecutionCounter replayed = new();
        ExecutionCounter seeded = new();

        string expected = SerializeGeth(chain, TraceOneThroughTraceEnvironment(chain, parent, block, target, GethTracer(chain, block, options), seeds: null, replayed));
        string actual = SerializeGeth(chain, TraceOneThroughTraceEnvironment(chain, parent, block, target, GethTracer(chain, block, options), chain.Container.Resolve<IPrefixStateSeedSource>(), seeded));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(seeded.Calls, Is.EqualTo(1), "a valid list seeds the last transaction whether it is carried or stored");
            Assert.That(actual, Is.EqualTo(expected), "the seeded prestate diff equals the replay");
        }
    }

    [TestCase(false, TestName = "AccessListSeed_WhenTheListIsPruned_ReplaysThePrefixWithTheSameTraces")]
    [TestCase(true, TestName = "AccessListSeed_WhenTheStoredListDoesNotMatchTheHeader_ReplaysThePrefixWithTheSameTraces")]
    public async Task AccessListSeed_WhenTheListCannotBeTrusted_ReplaysThePrefixWithTheSameTraces(bool corrupt)
    {
        using BasicTestBlockchain chain = await CreateAccessListSeedChain();
        BlockHeader parent = chain.BlockTree.Head!.Header;
        Block block = Historical(await AddSharedStateBlock(chain));
        IBlockAccessListStore store = chain.Container.Resolve<IBlockAccessListStore>();
        store.Delete((ulong)block.Number, block.Hash!);
        if (corrupt) store.Insert((ulong)block.Number, block.Hash!, new ReadOnlyBlockAccessList());
        Assert.That(store.Get((ulong)block.Number, block.Hash!)?.WireHash, Is.Not.EqualTo(block.Header.BlockAccessListHash), "precondition: nothing the header commits to is left");
        IPrefixStateSeedSource seeds = chain.Container.Resolve<IPrefixStateSeedSource>();
        Hash256 target = block.Transactions[^1].Hash!;
        GethTraceOptions options = new() { TxHash = target, Tracer = "prestateTracer", TracerConfig = JsonDocument.Parse("{\"diffMode\":true}").RootElement };
        ExecutionCounter replayed = new();
        ExecutionCounter seeded = new();
        ExecutionCounter parallelExecutions = new();
        using ParallelTraceBudget budget = new(2);
        using ParallelBlockTracer parallel = new(() => BuildParallelEnvironment(chain, executions: parallelExecutions), seeds, Budgets(chain, budget), LimboLogs.Instance);

        string expected = SerializeGeth(chain, TraceOneThroughTraceEnvironment(chain, parent, block, target, GethTracer(chain, block, options), seeds: null, replayed));
        string actual = SerializeGeth(chain, TraceOneThroughTraceEnvironment(chain, parent, block, target, GethTracer(chain, block, options), seeds, seeded));
        bool tracedInParallel = parallel.TryTrace(block, parent,
            (state, txHash) => GethStyleTracer.CreateOptionsTracer(block.Header, options with { TxHash = txHash }, state, chain.SpecProvider),
            afterTransactions: null, CancellationToken.None, out _);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(seeded.Calls, Is.EqualTo(block.Transactions.Length), "without a list the header commits to, the prefix is replayed");
            Assert.That(actual, Is.EqualTo(expected), "the fallback trace equals the replay");
            Assert.That(tracedInParallel, Is.False, "a block whose list cannot be trusted is left to the sequential replay");
            Assert.That(parallelExecutions.Calls, Is.Zero, "the parallel tracer declines before executing anything");
        }
    }

    [TestCase("callTracer")]
    [TestCase("prestateTracer")]
    [TestCase(null, TestName = "AccessListSeed_ParallelBlockTrace_TracesEachTransactionOnceLikeTheReplay(structLogs)")]
    public async Task AccessListSeed_ParallelBlockTrace_TracesEachTransactionOnceLikeTheReplay(string? tracerName)
    {
        using BasicTestBlockchain chain = await CreateAccessListSeedChain();
        BlockHeader parent = chain.BlockTree.Head!.Header;
        Block block = Historical(await AddSharedStateBlock(chain));
        IPrefixStateSeedSource seeds = chain.Container.Resolve<IPrefixStateSeedSource>();
        GethTraceOptions traceOptions = new() { Tracer = tracerName! };
        ExecutionCounter executions = new();
        using ParallelTraceBudget budget = new(3);
        using ParallelBlockTracer parallel = new(() => BuildParallelEnvironment(chain, executions: executions), seeds, Budgets(chain, budget), LimboLogs.Instance);

        string expected = chain.JsonSerializer.Serialize(new GethLikeTxTraceCollection(TraceWholeBlockThroughTraceEnvironment(chain, parent, block,
            state => GethStyleTracer.CreateOptionsTracer(block.Header, traceOptions, state, chain.SpecProvider))));
        bool traced = parallel.TryTrace(block, parent,
            (state, txHash) => GethStyleTracer.CreateOptionsTracer(block.Header, traceOptions with { TxHash = txHash }, state, chain.SpecProvider),
            afterTransactions: null, CancellationToken.None, out IReadOnlyList<GethLikeTxTrace>? traces);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(traced, Is.True, "a block with a valid access list is traced in parallel");
            Assert.That(traces!, Has.Count.EqualTo(block.Transactions.Length));
            Assert.That(chain.JsonSerializer.Serialize(new GethLikeTxTraceCollection(traces!)), Is.EqualTo(expected), "each transaction traced alone on its seeded state equals the replay");
            Assert.That(executions.Calls, Is.EqualTo(block.Transactions.Length), "every transaction executes once: a refused seed would replay its prefix and show up here");
        }
    }

    [Test]
    public async Task AccessListSeed_ParallelBlockTrace_TracesTheRewardsOnTheStateAfterTheLastTransaction([Values] bool stream)
    {
        using BasicTestBlockchain chain = await CreateAccessListSeedChain(builder => builder.AddSingleton<IRewardCalculatorSource>(new ZeroRewardToTheBeneficiary()));
        BlockHeader parent = chain.BlockTree.Head!.Header;
        Block block = Historical(await AddSharedStateBlock(chain));
        IPrefixStateSeedSource seeds = chain.Container.Resolve<IPrefixStateSeedSource>();
        ParityTraceTypes types = ParityTraceTypes.Trace | ParityTraceTypes.StateDiff | ParityTraceTypes.Rewards;
        ExecutionCounter executions = new();
        using ParallelTraceBudget budget = new(3);
        using ParallelBlockTracer parallel = new(() => BuildParallelEnvironment(chain, executions: executions), seeds, Budgets(chain, budget), LimboLogs.Instance);

        IReadOnlyCollection<ParityLikeTxTrace> sequential = TraceWholeBlockThroughTraceEnvironment(chain, parent, block, _ => new ParityLikeBlockTracer(types));
        List<ParityLikeTxTrace> emitted = [];
        IReadOnlyList<ParityLikeTxTrace>? traces = null;
        bool traced = stream
            ? parallel.TryStream(block, parent,
                (_, txHash) => new ParityLikeBlockTracer(txHash, types & ~ParityTraceTypes.Rewards),
                _ => new ParityLikeBlockTracer(types), batch => emitted.AddRange(batch), CancellationToken.None)
            : parallel.TryTrace(block, parent,
                (_, txHash) => new ParityLikeBlockTracer(txHash, types & ~ParityTraceTypes.Rewards),
                _ => new ParityLikeBlockTracer(types), CancellationToken.None, out traces);
        if (stream) traces = emitted;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(traced, Is.True, "a block with a valid access list is traced in parallel, rewards included");
            Assert.That(sequential.Count, Is.EqualTo(block.Transactions.Length + 1), "precondition: the transactions and the reward");
            Assert.That(chain.JsonSerializer.Serialize(ParityTxTraceFromStore.FromTxTrace(traces!)),
                Is.EqualTo(chain.JsonSerializer.Serialize(ParityTxTraceFromStore.FromTxTrace(sequential))),
                "each transaction and the reward, traced on the seeded state, equal the replay");
            Assert.That(executions.Calls, Is.EqualTo(block.Transactions.Length), "the reward pass is seeded with the end of the block, so no transaction runs twice");
        }
    }

    [TestCase(1, TestName = "AccessListSeed_WhenTheDelegationTheTargetRunsWasReplacedLaterInTheBlock_ServesItsCodeFromTheList")]
    [TestCase(3, TestName = "AccessListSeed_WhenTheTargetRunsTheLastDelegation_MatchesTheReplay")]
    public async Task AccessListSeed_WhenAnAccountChangesCodeTwiceInOneBlock_TracesEachDelegationLikeTheReplay(int targetIndex)
    {
        // A block validated from its access list persists only the code each account ends the block with, so the
        // delegation to the first contract is dropped from the code database here, as that validation leaves it.
        using BasicTestBlockchain chain = await CreateAccessListSeedChain(builder => builder.WithGenesisPostProcessor((_, state) =>
        {
            state.CreateAccount(SecondContract, 0);
            state.InsertCode(SecondContract, SecondContractCode, Amsterdam.Instance);
        }));
        BlockHeader parent = chain.BlockTree.Head!.Header;
        Block block = Historical(await AddRedelegationBlock(chain));
        ValueHash256 firstDelegation = ValueKeccak.Compute([.. Eip7702Constants.DelegationHeader, .. SharedContract.Bytes]);
        chain.DbProvider.CodeDb.Remove(firstDelegation.Bytes);
        Assert.That(chain.DbProvider.CodeDb.KeyExists(firstDelegation.Bytes), Is.False, "precondition: only the last delegation of the block is in the code database");
        IPrefixStateSeedSource seeds = chain.Container.Resolve<IPrefixStateSeedSource>();
        Hash256 target = block.Transactions[targetIndex].Hash!;
        GethTraceOptions prestate = new() { TxHash = target, Tracer = "prestateTracer", TracerConfig = JsonDocument.Parse("{\"diffMode\":true}").RootElement };
        GethTraceOptions calls = new() { TxHash = target, Tracer = "callTracer" };
        ExecutionCounter replayed = new();
        ExecutionCounter seeded = new();

        string expected = SerializeGeth(chain, TraceOneThroughTraceEnvironment(chain, parent, block, target, GethTracer(chain, block, prestate), seeds: null, replayed))
            + SerializeGeth(chain, TraceOneThroughTraceEnvironment(chain, parent, block, target, GethTracer(chain, block, calls), seeds: null, replayed));
        string actual = SerializeGeth(chain, TraceOneThroughTraceEnvironment(chain, parent, block, target, GethTracer(chain, block, prestate), seeds, seeded))
            + SerializeGeth(chain, TraceOneThroughTraceEnvironment(chain, parent, block, target, GethTracer(chain, block, calls), seeds, seeded));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(seeded.Calls, Is.EqualTo(2), "both traces are seeded, so only the target executes in each");
            Assert.That(actual, Is.EqualTo(expected), "the seeded account runs the delegation the list records, with its code read from the list");
        }
    }

    [Test]
    public async Task AccessListSeed_WhenTheListIsPruned_NeverAsksTheSourceUnderneathAndReplays()
    {
        RefusingSeedSource underneath = new();
        using BasicTestBlockchain chain = await CreatePrefixReplayChain(Amsterdam.Instance, underneath, builder => builder
            .AddDecorator<IPrefixStateSeedSource, BlockAccessListPrefixStateSeedSource>());
        BlockHeader parent = chain.BlockTree.Head!.Header;
        Block block = Historical(await AddThreeTransferBlock(chain));
        chain.Container.Resolve<IBlockAccessListStore>().Delete((ulong)block.Number, block.Hash!);
        Hash256 target = block.Transactions[^1].Hash!;
        GethTraceOptions options = new() { TxHash = target, Tracer = "callTracer" };
        ExecutionCounter executions = new();

        TraceOneThroughTraceEnvironment(chain, parent, block, target, GethTracer(chain, block, options), chain.Container.Resolve<IPrefixStateSeedSource>(), executions).DisposeItems();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(underneath.AskedFor, Is.EqualTo(-1), "a block carrying an access list is seeded from it or not at all");
            Assert.That(executions.Calls, Is.EqualTo(block.Transactions.Length), "without its list the prefix is replayed");
        }
    }

    [Test]
    public async Task AccessListSeed_WhenATransactionWritesASlotTheSystemCallWrote_ReplaysThePrefixWithTheSameTrace()
    {
        // The history contract here stores GAS at slot 0: the block's opening system call writes it at index 0, and the
        // first transaction calling it writes the same slot again. The system call runs again under every trace, so
        // its write sits in the world state above the overlay; the overlay is refused rather than shadowed by it.
        byte[] storeGasLeft = [(byte)Instruction.GAS, (byte)Instruction.PUSH0, (byte)Instruction.SSTORE];
        using BasicTestBlockchain chain = await CreateAccessListSeedChain(builder => builder.WithGenesisPostProcessor((_, state) =>
        {
            state.CreateAccount(Eip2935Constants.BlockHashHistoryAddress, 0, 1);
            state.InsertCode(Eip2935Constants.BlockHashHistoryAddress, storeGasLeft, Amsterdam.Instance);
        }));
        BlockHeader parent = chain.BlockTree.Head!.Header;
        Block block = Historical(await chain.AddBlock(
            Build.A.Transaction.WithTo(Eip2935Constants.BlockHashHistoryAddress).WithNonce(0).WithGasLimit(100_000).SignedAndResolved(TestItem.PrivateKeyB).TestObject,
            Build.A.Transaction.WithTo(Eip2935Constants.BlockHashHistoryAddress).WithNonce(0).WithGasLimit(200_000).SignedAndResolved(TestItem.PrivateKeyC).TestObject));
        Assert.That(block.Transactions.Length, Is.EqualTo(2), "precondition: both calls are in the block");
        Hash256 target = block.Transactions[1].Hash!;
        GethTraceOptions options = new() { TxHash = target, Tracer = "prestateTracer", TracerConfig = JsonDocument.Parse("{\"diffMode\":true}").RootElement };
        ExecutionCounter replayed = new();
        ExecutionCounter seeded = new();

        string expected = SerializeGeth(chain, TraceOneThroughTraceEnvironment(chain, parent, block, target, GethTracer(chain, block, options), seeds: null, replayed));
        string actual = SerializeGeth(chain, TraceOneThroughTraceEnvironment(chain, parent, block, target, GethTracer(chain, block, options), chain.Container.Resolve<IPrefixStateSeedSource>(), seeded));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(seeded.Calls, Is.EqualTo(2), "the overlay is refused and the prefix replayed");
            Assert.That(actual, Is.EqualTo(expected), "the target reads the slot the first transaction wrote, as in the replay");
        }
    }

    private static readonly Address SecondContract = new("0x00000000000000000000000000000000000c0de2");

    // slot0 = 1
    private static readonly byte[] SecondContractCode = [0x60, 0x01, 0x60, 0x00, 0x55, 0x00];

    /// <summary>An authority delegates to one contract, is called, re-delegates to another and is called again.</summary>
    private static async Task<Block> AddRedelegationBlock(BasicTestBlockchain chain)
    {
        ulong chainId = chain.SpecProvider.ChainId;
        EthereumEcdsa ecdsa = new(chainId);
        Transaction[] transactions =
        [
            Delegate(ecdsa, chainId, 0, SharedContract, 0),
            Build.A.Transaction.WithTo(Delegated).WithNonce(0).WithGasLimit(1_000_000).SignedAndResolved(TestItem.PrivateKeyB).TestObject,
            Delegate(ecdsa, chainId, 1, SecondContract, 1),
            Build.A.Transaction.WithTo(Delegated).WithNonce(1).WithGasLimit(1_000_000).SignedAndResolved(TestItem.PrivateKeyB).TestObject,
        ];

        Block block = await chain.AddBlock(transactions);
        Assert.That(block.Transactions.Length, Is.EqualTo(transactions.Length), "precondition: the block must hold every transaction of the scenario");
        return block;

        static Transaction Delegate(EthereumEcdsa ecdsa, ulong chainId, ulong senderNonce, Address codeSource, ulong authorityNonce) =>
            Build.A.Transaction.WithType(TxType.SetCode).WithChainId(chainId).WithTo(TestItem.AddressA).WithNonce(senderNonce).WithGasLimit(1_000_000)
                .WithMaxFeePerGas(1.GWei).WithMaxPriorityFeePerGas(1)
                .WithAuthorizationCode(ecdsa.Sign(TestItem.PrivateKeyD, chainId, codeSource, authorityNonce))
                .SignedAndResolved(ecdsa, TestItem.PrivateKeyC).TestObject;
    }

    private static Task<BasicTestBlockchain> CreateAccessListSeedChain(Action<ContainerBuilder>? configure = null) =>
        CreatePrefixReplayChain(Amsterdam.Instance, configure: builder =>
        {
            builder
                .AddSingleton<IPrefixStateSeedSource>(NullPrefixStateSeedSource.Instance)
                .AddDecorator<IPrefixStateSeedSource, BlockAccessListPrefixStateSeedSource>()
                .WithGenesisPostProcessor((_, state) =>
                {
                    state.CreateAccount(SharedContract, 0);
                    state.InsertCode(SharedContract, SharedContractCode, Amsterdam.Instance);
                    state.Set(new StorageCell(SharedContract, UInt256.One), 5);
                });
            configure?.Invoke(builder);
        });

    /// <summary>One block whose transactions write shared state: a contract counts, clears a slot for a refund and
    /// uses transient storage; a creation self-destructs into a fresh account; an EIP-7702 delegation to the contract
    /// is followed by a call into the delegated account; an EIP-7002 withdrawal request is queued for the system call
    /// after the transactions to dequeue; a transfer reaches the account the self-destruct created.</summary>
    private static async Task<Block> AddSharedStateBlock(BasicTestBlockchain chain)
    {
        ulong chainId = chain.SpecProvider.ChainId;
        EthereumEcdsa ecdsa = new(chainId);
        byte[] selfDestructingInit = [0x73, .. Beneficiary.Bytes, 0xff];
        Transaction[] transactions =
        [
            Call(TestItem.PrivateKeyB, 0, SharedContract, 0),
            Call(TestItem.PrivateKeyC, 0, SharedContract, 3),
            Build.A.Transaction.WithCode(selfDestructingInit).WithValue(1).WithNonce(1).WithGasLimit(1_000_000).SignedAndResolved(TestItem.PrivateKeyB).TestObject,
            Build.A.Transaction.WithType(TxType.SetCode).WithChainId(chainId).WithTo(Delegated).WithNonce(1).WithGasLimit(1_000_000)
                .WithMaxFeePerGas(1.GWei).WithMaxPriorityFeePerGas(1)
                .WithAuthorizationCode(ecdsa.Sign(TestItem.PrivateKeyD, chainId, SharedContract, 0))
                .SignedAndResolved(ecdsa, TestItem.PrivateKeyC).TestObject,
            Call(TestItem.PrivateKeyB, 2, Delegated, 2),
            Call(TestItem.PrivateKeyC, 2, SharedContract, 0),
            Build.A.Transaction.WithTo(Eip7002Constants.WithdrawalRequestPredeployAddress).WithData(new byte[56]).WithValue(1_000_000)
                .WithNonce(3).WithGasLimit(1_000_000).SignedAndResolved(TestItem.PrivateKeyC).TestObject,
            Call(TestItem.PrivateKeyB, 3, Beneficiary, 1),
        ];

        Block block = await chain.AddBlock(transactions);
        Assert.That(block.Transactions.Length, Is.EqualTo(transactions.Length), "precondition: the block must hold every transaction of the scenario");
        Assert.That(block.Header.BlockAccessListHash, Is.Not.Null, "precondition: an Amsterdam block commits to its access list");
        return block;

        static Transaction Call(PrivateKey sender, ulong nonce, Address to, int value) =>
            Build.A.Transaction.WithTo(to).WithNonce(nonce).WithValue((UInt256)value).WithGasLimit(1_000_000).SignedAndResolved(sender).TestObject;
    }

    /// <summary>The block as a trace of history finds it: header and body, with the access list left in its store.</summary>
    private static Block Historical(Block block) => new(block.Header, block.Body);

    private static Func<IWorldState, IBlockTracer<GethLikeTxTrace>> GethTracer(BasicTestBlockchain chain, Block block, GethTraceOptions options) =>
        state => GethStyleTracer.CreateOptionsTracer(block.Header, options, state, chain.SpecProvider);

    private static string SerializeGeth(BasicTestBlockchain chain, IReadOnlyCollection<GethLikeTxTrace> traces)
    {
        using GethLikeTxTraceCollection collection = new(traces);
        return chain.JsonSerializer.Serialize(collection);
    }

    /// <summary>Traces one transaction the way the RPC does, in its own read-only environment, counting every
    /// transaction the processor executes, whether block processing reached it through the access list manager or not.</summary>
    private static IReadOnlyCollection<TTrace> TraceOneThroughTraceEnvironment<TTrace>(BasicTestBlockchain chain, BlockHeader parent, Block block, Hash256 target,
        Func<IWorldState, IBlockTracer<TTrace>> tracerFor, IPrefixStateSeedSource? seeds, ExecutionCounter executions)
    {
        IBlockValidationModule[] validation = chain.Container.Resolve<IBlockValidationModule[]>();
        IOverridableEnv env = chain.Container.Resolve<ITraceEnvFactory>().CreateForTracing();
        using ILifetimeScope scope = chain.Container.BeginLifetimeScope(builder => builder
            .AddModule(validation)
            .AddModule(new TransactionTraceModule(validation))
            .AddModule(env)
            .AddDecorator<TransactionProcessorAdapterFactory>((_, inner) => processor => new CountingTransactionAdapter(inner(processor), executions)));
        BlockchainProcessorFacade processor = scope.Resolve<BlockchainProcessorFacade>();
        using IDisposable pinned = env.BuildAndOverride(parent);
        IBlockTracer<TTrace> tracer = tracerFor(scope.Resolve<IWorldState>());
        processor.Process(block, TraceProcessingOptions.ReadOnlyReplay, TransactionTraceBoundary.Wrap(tracer, target, seeds), CancellationToken.None);
        return tracer.BuildResult();
    }
}
