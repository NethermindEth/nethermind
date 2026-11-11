// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Consensus.Stateless;
using Nethermind.Consensus.Tracing;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Container;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.JsonRpc.Modules.Proof;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.State.Flat.History.Changesets;
using Nethermind.State.OverridableEnv;
using Nethermind.TxPool;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using NUnit.Framework.Constraints;
using FlatHistoryColumns = Nethermind.State.Flat.FlatHistoryColumns;
using ResultType = Nethermind.Core.ResultType;

namespace Nethermind.JsonRpc.Test.Modules;

/// <summary>Read-only query modules configure the environments serving their kinds of query, and never block processing.</summary>
[TestFixture]
[Parallelizable(ParallelScope.Self)]
public class ReadOnlyQueryModuleTests
{
    private static readonly Address OverrideTarget = new("0xc2000000000000000000000000000000000000c1");

    /// <summary>The contracts the transactions of <see cref="AddBlockCallingEachContract"/> call, one each, so an offer names its transaction.</summary>
    private static readonly Address[] Contracts =
    [
        new("0xc0000000000000000000000000000000000000c0"),
        new("0xc0000000000000000000000000000000000000c1"),
        new("0xc0000000000000000000000000000000000000c2"),
    ];

    /// <summary>The contract of the transaction the tests trace, which has an untraced transaction on either side.</summary>
    private static Address Target => Contracts[1];

    private const string StructLogger = "";
    private const string JavaScriptTracer = "{data:[],fault:function(){},step:function(log){this.data.push(log.op.toString())},result:function(){return this.data}}";

    private static byte[] Returning42() => Prepare.EvmCode.PushData(42).PushData(0).Op(Instruction.MSTORE).Return(32, 0).Done;

    /// <summary>What <see cref="Returning42"/> and <see cref="CountingContract"/> return.</summary>
    private static byte[] Returning42Word()
    {
        byte[] word = new byte[32];
        word[^1] = 42;
        return word;
    }

    /// <summary>Counts its calls in slot 0, hashes a word and returns 42.</summary>
    private static byte[] CountingContract() => Prepare.EvmCode
        .PushData(0).Op(Instruction.SLOAD).PushData(1).Op(Instruction.ADD).PushData(0).Op(Instruction.SSTORE)
        .PushData(42).PushData(0).Op(Instruction.MSTORE)
        .PushData(32).PushData(0).Op(Instruction.KECCAK256).Op(Instruction.POP)
        .Return(32, 0).Done;

    private static Task<TestRpcBlockchain> Chain(FrameOfferProbe probe, ReadOnlyQueryKind appliesTo, Action<ContainerBuilder>? configure = null) =>
        Chain([probe.ModuleFor(appliesTo)], configure);

    /// <summary>A Prague chain, whose blocks end with the execution request system calls, with <see cref="Contracts"/> deployed at genesis.</summary>
    private static Task<TestRpcBlockchain> Chain(IReadOnlyQueryModule[] modules, Action<ContainerBuilder>? configure = null) =>
        TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build(builder =>
        {
            builder
                .AddSingleton<ISpecProvider>(new TestSpecProvider(Prague.Instance) { AllowTestChainOverride = false })
                .WithGenesisPostProcessor((_, state, specProvider) =>
                {
                    foreach (Address contract in Contracts)
                    {
                        state.CreateAccount(contract, UInt256.Zero);
                        state.InsertCode(contract, CountingContract(), specProvider.GenesisSpec);
                    }
                });
            foreach (IReadOnlyQueryModule module in modules)
                builder.AddSingleton<IReadOnlyQueryModule>(module);
            configure?.Invoke(builder);
        });

    /// <summary>Makes a <see cref="Chain"/> an Amsterdam one, whose blocks carry block-level access lists.</summary>
    /// <remarks>Each contract counts on from one: setting a fresh slot costs more state gas (EIP-8037) than the calls carry.</remarks>
    private static void WithBlockLevelAccessLists(ContainerBuilder builder) => builder
        .AddSingleton<ISpecProvider>(new TestSpecProvider(Amsterdam.Instance) { AllowTestChainOverride = false })
        .WithGenesisPostProcessor((_, state) =>
        {
            foreach (Address contract in Contracts)
                state.Set(new StorageCell(contract, UInt256.Zero), UInt256.One);
        });

    /// <summary>Adds a block whose transaction <c>i</c> calls <c>Contracts[i]</c>.</summary>
    private static async Task<Block> AddBlockCallingEachContract(TestRpcBlockchain chain)
    {
        ulong nonce = chain.WorldStateManager.GlobalStateReader.GetNonce(chain.BlockTree.Head!.Header, TestItem.AddressB);
        Transaction[] transactions = Contracts.Select((contract, i) => Build.A.Transaction.WithTo(contract).WithNonce(nonce + (ulong)i)
            .WithGasLimit(100_000).SignedAndResolved(TestItem.PrivateKeyB).TestObject).ToArray();
        Block block = await chain.AddBlock(transactions);
        Assert.That(block.Transactions, Has.Length.EqualTo(Contracts.Length), "precondition: the block calls each contract once");
        return block;
    }

    private static LegacyTransactionForRpc Query(TestRpcBlockchain chain, Address to) =>
        new(Build.A.Transaction.WithTo(to).WithGasLimit(100_000).WithGasPrice(0).SignedAndResolved(TestItem.PrivateKeyB).TestObject,
            new(chain.SpecProvider.ChainId));

    private static object CallTo(Address to) => new { from = TestItem.AddressB.ToString(), to = to.ToString(), gas = "0x186a0" };

    private static object? GethOptions(string? tracer) => tracer switch
    {
        null => null,
        StructLogger => new { },
        _ => new { tracer },
    };

    /// <summary>Runs <paramref name="method"/> against <c>block</c>, its middle transaction or a call to <see cref="Target"/>.</summary>
    /// <param name="tracer">The Geth tracer, <see cref="StructLogger"/> for the struct logger, or the Parity trace type.</param>
    private static Task<string> Trace(TestRpcBlockchain chain, string method, Block block, string? tracer)
    {
        Hash256 target = block.Transactions[1].Hash!;
        string[] types = [tracer ?? "trace"];
        return method switch
        {
            "debug_traceCall" => RpcTest.TestSerializedRequest(chain.DebugRpcModule, method, CallTo(Target), "latest", GethOptions(tracer)),
            "debug_traceCallMany" => RpcTest.TestSerializedRequest(chain.DebugRpcModule, method,
                new[] { new { transactions = new[] { CallTo(Target) } } }, "latest", GethOptions(tracer)),
            "debug_traceTransaction" => RpcTest.TestSerializedRequest(chain.DebugRpcModule, method, target, GethOptions(tracer)),
            "debug_traceTransactionByBlockAndIndex" => RpcTest.TestSerializedRequest(chain.DebugRpcModule, method, "latest", 1, GethOptions(tracer)),
            "debug_traceBlockByNumber" => RpcTest.TestSerializedRequest(chain.DebugRpcModule, method, "latest", GethOptions(tracer)),
            "debug_traceBlockByHash" => RpcTest.TestSerializedRequest(chain.DebugRpcModule, method, block.Hash!, GethOptions(tracer)),
            "debug_traceBlock" => RpcTest.TestSerializedRequest(chain.DebugRpcModule, method, Rlp.Encode(block).ToString(), GethOptions(tracer)),
            "trace_call" => RpcTest.TestSerializedRequest(chain.TraceRpcModule, method, CallTo(Target), types, "latest"),
            "trace_callMany" => RpcTest.TestSerializedRequest(chain.TraceRpcModule, method, new[] { new[] { CallTo(Target), types } }, "latest"),
            "trace_rawTransaction" => RpcTest.TestSerializedRequest(chain.TraceRpcModule, method, Encoded(SignedCallTo(chain, Target)), types),
            "trace_replayTransaction" => RpcTest.TestSerializedRequest(chain.TraceRpcModule, method, target, types),
            "trace_replayBlockTransactions" => RpcTest.TestSerializedRequest(chain.TraceRpcModule, method, "latest", types),
            "trace_block" => RpcTest.TestSerializedRequest(chain.TraceRpcModule, method, "latest"),
            "trace_transaction" => RpcTest.TestSerializedRequest(chain.TraceRpcModule, method, target),
            "proof_getTransactionReceipt" => RpcTest.TestSerializedRequest(chain.ProofRpcModule, method, target, true),
            _ => throw new ArgumentOutOfRangeException(nameof(method), method, null),
        };
    }

    /// <summary>A transaction from <see cref="TestItem.AddressB"/> at its next nonce calling <paramref name="to"/>.</summary>
    private static Transaction SignedCallTo(TestRpcBlockchain chain, Address to)
    {
        ulong nonce = chain.WorldStateManager.GlobalStateReader.GetNonce(chain.BlockTree.Head!.Header, TestItem.AddressB);
        return Build.A.Transaction.WithTo(to).WithNonce(nonce).WithGasLimit(100_000).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
    }

    /// <summary><paramref name="transaction"/> encoded for trace_rawTransaction.</summary>
    private static byte[] Encoded(Transaction transaction) => TxDecoder.Instance.Encode(transaction, RlpBehaviors.SkipTypedWrapping).Bytes;

    /// <summary>Runs <paramref name="method"/> with one call to <see cref="Target"/> in one simulated block.</summary>
    private static Task<string> Simulate(TestRpcBlockchain chain, string method)
    {
        object payload = new { blockStateCalls = new[] { new { calls = new[] { CallTo(Target) } } } };
        return method switch
        {
            "eth_simulateV1" => chain.TestEthRpc(method, payload, "latest"),
            "debug_simulateV1" => RpcTest.TestSerializedRequest(chain.DebugRpcModule, method, payload, "latest", new { tracer = "callTracer" }),
            // A bundle carrying its own override is simulated; without one the bundles are traced in the debug environments.
            // Called directly: the serializer metadata the harness checks has no entry for the projection it returns.
            "debug_traceCallMany" => Task.FromResult(Unserialized(chain.DebugRpcModule.debug_traceCallMany(
                [new TransactionBundle { Transactions = [new LegacyTransactionForRpc { From = TestItem.AddressB, To = Target, Gas = 100_000 }], BlockOverride = new BlockOverride { GasLimit = 30_000_000 } }],
                BlockParameter.Latest, new GethTraceOptions { Tracer = "callTracer" }))),
            "trace_simulateV1" => RpcTest.TestSerializedRequest(chain.TraceRpcModule, method, payload, "latest", new[] { "trace" }),
            _ => throw new ArgumentOutOfRangeException(nameof(method), method, null),
        };
    }

    /// <summary>One request served by each of the query environments the module factories build.</summary>
    private static async Task<string[]> SendToEachEnvironment(TestRpcBlockchain chain, Block block)
    {
        Dictionary<Address, AccountOverride> overrides = new() { [OverrideTarget] = new() { Code = Returning42() } };
        return
        [
            await chain.TestEthRpc("eth_call", CallTo(Target), "latest"),
            await chain.TestEthRpc("eth_call", CallTo(OverrideTarget), "latest", overrides),
            await Simulate(chain, "eth_simulateV1"),
            await Trace(chain, "debug_traceCall", block, "callTracer"),
            await Trace(chain, "trace_call", block, "trace"),
            await Trace(chain, "trace_replayBlockTransactions", block, "trace"),
            await Trace(chain, "proof_getTransactionReceipt", block, null),
        ];
    }

    /// <summary>Traces <paramref name="block"/> whole through the debug and the trace module: on the workers of their
    /// parallel tracers once the block is indexed, in their own environments before that.</summary>
    private static async Task<string[]> TraceWholeBlock(TestRpcBlockchain chain, Block block) =>
    [
        await RpcTest.TestSerializedRequest(chain.DebugRpcModule, "debug_traceBlockByNumber", block.Number, new { tracer = "callTracer" }),
        await RpcTest.TestSerializedRequest(chain.TraceRpcModule, "trace_replayBlockTransactions", block.Hash!, new[] { "trace" }),
    ];

    private static void AssertSucceeded(string response) => Assert.That(JToken.Parse(response)["error"], Is.Null, response);

    /// <summary>A response <see cref="AssertSucceeded"/> reads, for a result returned without serializing it.</summary>
    private static string Unserialized(IResultWrapper result) =>
        result.Result.ResultType == ResultType.Success ? "{}" : new JObject { ["error"] = result.Result.Error }.ToString();

    [Test]
    public async Task Calls_offer_frames_and_block_processing_never_does()
    {
        FrameOfferProbe probe = new();
        using TestRpcBlockchain chain = await Chain(probe, ReadOnlyQueryKind.Call);

        // Deploying a contract runs its init code in block production and processing.
        ulong nonce = chain.WorldStateManager.GlobalStateReader.GetNonce(chain.BlockTree.Head!.Header, TestItem.AddressB);
        Transaction deploy = Build.A.Transaction.WithCode(Prepare.EvmCode.ForInitOf(Returning42()).Done).WithGasLimit(1_000_000)
            .WithNonce(nonce).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        Block deployment = await chain.AddBlock(deploy);
        Address deployed = ContractAddress.From(TestItem.AddressB, nonce);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(deployment.Transactions, Has.Length.EqualTo(1), "precondition: the deployment is included");
            Assert.That(probe.Offers, Is.Zero, "block production and processing must not run on a query module's runner");
        }

        string call = await chain.TestEthRpc("eth_call", Query(chain, deployed), "latest");
        int afterCall = probe.Offers;
        Dictionary<Address, AccountOverride> overrides = new() { [OverrideTarget] = new() { Code = Returning42() } };
        string overridden = await chain.TestEthRpc("eth_call", Query(chain, OverrideTarget), "latest", overrides);
        int afterOverride = probe.Offers;
        string estimate = await chain.TestEthRpc("eth_estimateGas", Query(chain, deployed), "latest");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(JToken.Parse(call)["result"]?.Value<string>(), Does.EndWith("2a"), call);
            Assert.That(JToken.Parse(overridden)["result"]?.Value<string>(), Does.EndWith("2a"), overridden);
            Assert.That(JToken.Parse(estimate)["result"], Is.Not.Null, estimate);
            Assert.That(afterCall, Is.GreaterThan(0), "eth_call runs in the shared read-only environments");
            Assert.That(afterOverride, Is.GreaterThan(afterCall), "a call with a state override runs in the per-call environment");
            Assert.That(probe.Offers, Is.GreaterThan(afterOverride), "eth_estimateGas runs in the shared read-only environments");
        }
    }

    [Test]
    public async Task A_module_for_no_query_kind_is_loaded_nowhere()
    {
        FrameOfferProbe nowhere = new();
        FrameOfferProbe everywhere = new();
        using ParallelTracing parallel = new();
        using TestRpcBlockchain chain = await Chain(
            [nowhere.ModuleFor(ReadOnlyQueryKind.None), everywhere.ModuleFor(ReadOnlyQueryKind.All)], parallel.Configure);
        BlockHeader parent = chain.BlockTree.Head!.Header;
        Block block = await AddBlockCallingEachContract(chain);

        string[] responses = await SendToEachEnvironment(chain, block);
        HashSet<object> moduleMachines = everywhere.Machines();
        parallel.Index(chain, block, parent);
        string[] onTheWorkers = await TraceWholeBlock(chain, block);

        using (Assert.EnterMultipleScope())
        {
            foreach (string response in responses.Concat(onTheWorkers)) AssertSucceeded(response);
            Assert.That(everywhere.Machines().Except(moduleMachines), Is.Not.Empty, "precondition: the indexed block is traced on the parallel tracer's workers");
            Assert.That(nowhere.Loads, Is.Zero, "a module applies only to the query kinds it names");
            Assert.That(nowhere.Offers, Is.Zero, "a module applies only to the query kinds it names");
        }
    }

    [Test]
    public async Task Each_kind_is_loaded_only_into_its_own_environments()
    {
        FrameOfferProbe calls = new();
        FrameOfferProbe simulations = new();
        FrameOfferProbe traces = new();
        FrameOfferProbe[] probes = [calls, simulations, traces];
        using TestRpcBlockchain chain = await Chain([
            calls.ModuleFor(ReadOnlyQueryKind.Call),
            simulations.ModuleFor(ReadOnlyQueryKind.Simulate),
            traces.ModuleFor(ReadOnlyQueryKind.Trace)]);
        Block block = await AddBlockCallingEachContract(chain);
        Dictionary<Address, AccountOverride> overrides = new() { [OverrideTarget] = new() { Code = CountingContract() } };

        (string Request, FrameOfferProbe Expected, Address Called, Func<Task<string>> Send)[] requests =
        [
            ("eth_call", calls, Target, () => chain.TestEthRpc("eth_call", CallTo(Target), "latest")),
            ("eth_call with a state override", calls, OverrideTarget, () => chain.TestEthRpc("eth_call", CallTo(OverrideTarget), "latest", overrides)),
            ("eth_estimateGas", calls, Target, () => chain.TestEthRpc("eth_estimateGas", CallTo(Target), "latest")),
            ("eth_simulateV1", simulations, Target, () => Simulate(chain, "eth_simulateV1")),
            ("debug_traceCallMany with a block override", simulations, Target, () => Simulate(chain, "debug_traceCallMany")),
            ("trace_simulateV1", simulations, Target, () => Simulate(chain, "trace_simulateV1")),
            ("debug_traceCall", traces, Target, () => Trace(chain, "debug_traceCall", block, "callTracer")),
            ("debug_traceCallMany", traces, Target, () => Trace(chain, "debug_traceCallMany", block, "callTracer")),
            ("debug_traceCallMany with a state override in its trace options", traces, OverrideTarget, () => RpcTest.TestSerializedRequest(chain.DebugRpcModule,
                "debug_traceCallMany", new[] { new { transactions = new[] { CallTo(OverrideTarget) } } }, "latest", new { tracer = "callTracer", stateOverrides = overrides })),
            ("debug_traceBlockByNumber", traces, Target, () => Trace(chain, "debug_traceBlockByNumber", block, "callTracer")),
            ("trace_call", traces, Target, () => Trace(chain, "trace_call", block, "trace")),
            ("trace_replayBlockTransactions", traces, Target, () => Trace(chain, "trace_replayBlockTransactions", block, "trace")),
        ];

        foreach ((string request, FrameOfferProbe expected, Address called, Func<Task<string>> send) in requests)
        {
            foreach (FrameOfferProbe probe in probes) probe.Clear();
            string response = await send();

            using (Assert.EnterMultipleScope())
            {
                AssertSucceeded(response);
                foreach (FrameOfferProbe probe in probes)
                {
                    Assert.That(probe.OffersFor(called), probe == expected ? Is.GreaterThan(0) : (IResolveConstraint)Is.Zero,
                        $"{request} must reach the module of its own kind, and only that one");
                }
            }
        }
    }

    [Test]
    public async Task Every_environment_loads_the_modules_after_its_own_registrations()
    {
        TestSpecProvider specProvider = new(Prague.Instance) { AllowTestChainOverride = false };
        SpecProviderReplacement replacement = new(specProvider);
        using ParallelTracing parallel = new();
        using TestRpcBlockchain chain = await Chain([replacement], builder =>
        {
            builder.AddSingleton<ISpecProvider>(specProvider);
            parallel.Configure(builder);
        });
        BlockHeader parent = chain.BlockTree.Head!.Header;
        Block block = await AddBlockCallingEachContract(chain);

        string[] responses = await SendToEachEnvironment(chain, block);
        parallel.Index(chain, block, parent);
        string[] onTheWorkers = await TraceWholeBlock(chain, block);
        SpecProviderReplacement.LoadedEnvironment[] environments = replacement.Environments;

        using (Assert.EnterMultipleScope())
        {
            foreach (string response in responses.Concat(onTheWorkers)) AssertSucceeded(response);
            // The shared read-only and the per-override call environments, the simulate one, the debug module's, the
            // trace module's call and replay ones, and the proof module's; then the parallel tracers' workers.
            Assert.That(environments.Count(static environment => !environment.IsParallelTracerWorker), Is.GreaterThanOrEqualTo(7),
                "precondition: the module is loaded into every environment the requests ran in");
            Assert.That(environments.Count(static environment => environment.IsParallelTracerWorker), Is.GreaterThanOrEqualTo(2),
                "precondition: the module is loaded into the workers of the debug and trace modules' parallel tracers");
            Assert.That(environments.Where(static environment => !environment.ResolvesTheReplacement), Is.Empty,
                "what a module registers must take the place of what the environment registers itself, in every environment");
        }
    }

    [TestCase("debug_traceCall", "callTracer")]
    [TestCase("debug_traceCall", "4byteTracer")]
    [TestCase("debug_traceCall", "noopTracer")]
    [TestCase("debug_traceCallMany", "callTracer")]
    [TestCase("debug_traceTransaction", "callTracer")]
    [TestCase("debug_traceTransactionByBlockAndIndex", "callTracer")]
    [TestCase("debug_traceBlockByNumber", "callTracer")]
    [TestCase("debug_traceBlockByHash", "4byteTracer")]
    [TestCase("debug_traceBlock", "noopTracer")]
    [TestCase("trace_call", "trace")]
    [TestCase("trace_call", "stateDiff")]
    [TestCase("trace_callMany", "trace")]
    [TestCase("trace_rawTransaction", "trace")]
    [TestCase("trace_replayTransaction", "trace")]
    [TestCase("trace_replayTransaction", "stateDiff")]
    [TestCase("trace_replayBlockTransactions", "trace")]
    [TestCase("trace_block", null)]
    [TestCase("trace_transaction", null)]
    [TestCase("proof_getTransactionReceipt", null)]
    public async Task Traces_offer_frames_with_a_trace_module(string method, string? tracer)
    {
        FrameOfferProbe probe = new();
        using TestRpcBlockchain chain = await Chain(probe, ReadOnlyQueryKind.Trace);
        Block block = await AddBlockCallingEachContract(chain);
        Assert.That(probe.Offers, Is.Zero, "precondition: block production and processing never run on the runner");

        string response = await Trace(chain, method, block, tracer);

        using (Assert.EnterMultipleScope())
        {
            AssertSucceeded(response);
            Assert.That(probe.OffersFor(Target), Is.GreaterThan(0), $"{method} runs in a trace environment");
        }
    }

    [Test]
    public async Task Intermediate_roots_offer_frames_with_a_trace_module()
    {
        FrameOfferProbe probe = new();
        using TestRpcBlockchain chain = await Chain(probe, ReadOnlyQueryKind.Trace);
        Block block = await AddBlockCallingEachContract(chain);

        // Called directly: the serializer metadata the harness checks has no entry for the list it returns.
        using ResultWrapper<IReadOnlyCollection<Hash256>> roots = chain.DebugRpcModule.debug_intermediateRoots(block.Hash!);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(roots.Result.ResultType, Is.EqualTo(ResultType.Success), roots.Result.Error);
            Assert.That(roots.Data, Has.Count.EqualTo(block.Transactions.Length));
            Assert.That(Contracts.Select(probe.OffersFor), Has.All.GreaterThan(0), "debug_intermediateRoots replays the block in a trace environment");
        }
    }

    [TestCase("eth_simulateV1")]
    [TestCase("debug_simulateV1")]
    [TestCase("debug_traceCallMany")]
    [TestCase("trace_simulateV1")]
    public async Task Simulations_offer_frames_with_a_simulate_module(string method)
    {
        FrameOfferProbe probe = new();
        using TestRpcBlockchain chain = await Chain(probe, ReadOnlyQueryKind.Simulate);

        string response = await Simulate(chain, method);

        using (Assert.EnterMultipleScope())
        {
            AssertSucceeded(response);
            Assert.That(probe.OffersFor(Target), Is.GreaterThan(0), $"{method} runs in a simulate environment");
        }
    }

    // Blocks with block-level access lists run their transactions and system calls on machines of their own, which must
    // take the environment's runner as its own machine does. A call runs on the environment's machine alone.
    [TestCase(ReadOnlyQueryKind.Call, "eth_call", null)]
    [TestCase(ReadOnlyQueryKind.Trace, "debug_traceCall", "callTracer")]
    [TestCase(ReadOnlyQueryKind.Trace, "debug_traceTransaction", "callTracer")]
    [TestCase(ReadOnlyQueryKind.Trace, "debug_traceBlockByNumber", "callTracer")]
    [TestCase(ReadOnlyQueryKind.Trace, "trace_call", "trace")]
    [TestCase(ReadOnlyQueryKind.Trace, "trace_replayTransaction", "trace")]
    [TestCase(ReadOnlyQueryKind.Trace, "trace_replayBlockTransactions", "trace")]
    [TestCase(ReadOnlyQueryKind.Trace, "proof_getTransactionReceipt", null)]
    [TestCase(ReadOnlyQueryKind.Simulate, "eth_simulateV1", null)]
    [TestCase(ReadOnlyQueryKind.Simulate, "debug_simulateV1", null)]
    [TestCase(ReadOnlyQueryKind.Simulate, "debug_traceCallMany", null)]
    [TestCase(ReadOnlyQueryKind.Simulate, "trace_simulateV1", null)]
    public async Task Queries_offer_frames_on_a_chain_with_block_level_access_lists(ReadOnlyQueryKind kind, string method, string? tracer)
    {
        FrameOfferProbe probe = new();
        using TestRpcBlockchain chain = await Chain(probe, kind, WithBlockLevelAccessLists);
        Block block = await AddBlockCallingEachContract(chain);
        Assert.That(probe.Offers, Is.Zero, "precondition: block production and processing never run on the runner");

        string response = kind switch
        {
            ReadOnlyQueryKind.Call => await chain.TestEthRpc(method, CallTo(Target), "latest"),
            ReadOnlyQueryKind.Simulate => await Simulate(chain, method),
            _ => await Trace(chain, method, block, tracer),
        };

        using (Assert.EnterMultipleScope())
        {
            AssertSucceeded(response);
            Assert.That(probe.OffersFor(Target), Is.GreaterThan(0), $"{method} runs in an environment of the module's kind");
        }
    }

    // The target transaction is traced opcode by opcode; the one before it is replayed untraced.
    [TestCase("debug_traceTransaction", StructLogger)]
    [TestCase("debug_traceTransaction", "prestateTracer")]
    [TestCase("debug_traceTransaction", JavaScriptTracer)]
    [TestCase("debug_traceTransaction", "keccak256PreimageTracer")]
    [TestCase("debug_traceTransactionByBlockAndIndex", StructLogger)]
    [TestCase("trace_replayTransaction", "vmTrace")]
    public async Task Instruction_tracers_run_the_traced_transaction_on_the_interpreter(string method, string tracer)
    {
        FrameOfferProbe probe = new();
        using TestRpcBlockchain chain = await Chain(probe, ReadOnlyQueryKind.All);
        Block block = await AddBlockCallingEachContract(chain);

        string response = await Trace(chain, method, block, tracer);

        using (Assert.EnterMultipleScope())
        {
            AssertSucceeded(response);
            Assert.That(probe.OffersFor(Target), Is.Zero, "a transaction traced opcode by opcode must never reach the runner");
            Assert.That(probe.OffersFor(Contracts[0]), Is.GreaterThan(0), "the untraced transaction replayed before it still does");
        }
    }

    // A traced call and a block traced whole have no untraced transaction. A block traced whole ends with untraced
    // system calls; a traced call stops after the call, so nothing after it runs.
    [TestCase("debug_traceCall", StructLogger)]
    [TestCase("debug_traceCall", "prestateTracer")]
    [TestCase("debug_traceCall", JavaScriptTracer)]
    [TestCase("debug_traceBlockByNumber", StructLogger)]
    [TestCase("debug_traceBlockByHash", "prestateTracer")]
    [TestCase("trace_call", "vmTrace")]
    [TestCase("trace_replayBlockTransactions", "vmTrace")]
    public async Task Instruction_tracers_run_traced_calls_and_blocks_on_the_interpreter(string method, string tracer)
    {
        FrameOfferProbe probe = new();
        using TestRpcBlockchain chain = await Chain(probe, ReadOnlyQueryKind.All);
        Block block = await AddBlockCallingEachContract(chain);

        string response = await Trace(chain, method, block, tracer);

        using (Assert.EnterMultipleScope())
        {
            AssertSucceeded(response);
            foreach (Address contract in Contracts)
                Assert.That(probe.OffersFor(contract), Is.Zero, "a transaction traced opcode by opcode must never reach the runner");
            int systemCallOffers = probe.OffersFor(Eip7002Constants.WithdrawalRequestPredeployAddress) + probe.OffersFor(Eip7251Constants.ConsolidationRequestPredeployAddress);
            if (method == "debug_traceCall")
                Assert.That(systemCallOffers, Is.Zero, "a traced call stops before the execution request system calls that end the block");
            else
                Assert.That(systemCallOffers, Is.GreaterThan(0), "the execution request system calls that end the block still do");
        }
    }

    [Test]
    public async Task Block_traces_offer_frames_on_the_parallel_tracer_workers()
    {
        FrameOfferProbe probe = new();
        using ParallelTracing parallel = new();
        using TestRpcBlockchain chain = await Chain(probe, ReadOnlyQueryKind.Trace, parallel.Configure);
        BlockHeader parent = chain.BlockTree.Head!.Header;
        Block block = await AddBlockCallingEachContract(chain);
        IDebugRpcModule debug = chain.DebugRpcModule;
        ITraceRpcModule trace = chain.TraceRpcModule;

        // Not indexed yet, so each module replays the block in its own environment.
        string debugReplayed = await RpcTest.TestSerializedRequest(debug, "debug_traceBlockByNumber", block.Number, new { tracer = "callTracer" });
        string traceReplayed = await RpcTest.TestSerializedRequest(trace, "trace_replayBlockTransactions", block.Hash!, new[] { "trace" });
        HashSet<object> moduleMachines = probe.Machines();
        int[] replayedOffers = Contracts.Select(probe.OffersFor).ToArray();

        parallel.Index(chain, block, parent);
        probe.Clear();
        string debugParallel = await RpcTest.TestSerializedRequest(debug, "debug_traceBlockByNumber", block.Number, new { tracer = "callTracer" });
        HashSet<object> debugWorkers = probe.Machines();
        int[] debugOffers = Contracts.Select(probe.OffersFor).ToArray();
        probe.Clear();
        string traceParallel = await RpcTest.TestSerializedRequest(trace, "trace_replayBlockTransactions", block.Hash!, new[] { "trace" });
        HashSet<object> traceWorkers = probe.Machines();
        int[] traceOffers = Contracts.Select(probe.OffersFor).ToArray();

        using (Assert.EnterMultipleScope())
        {
            AssertSucceeded(debugParallel);
            AssertSucceeded(traceParallel);
            Assert.That(debugParallel, Is.EqualTo(debugReplayed), "the parallel trace is the replayed one");
            Assert.That(traceParallel, Is.EqualTo(traceReplayed), "the parallel trace is the replayed one");
            Assert.That(replayedOffers, Has.All.GreaterThan(0), "the replay runs in the module's environment");
            Assert.That(debugOffers, Has.All.GreaterThan(0), "every transaction of the indexed block is offered");
            Assert.That(traceOffers, Has.All.GreaterThan(0), "every transaction of the indexed block is offered");
            Assert.That(debugWorkers.Except(moduleMachines), Is.Not.Empty, "the indexed block is traced on the parallel tracer's workers");
            Assert.That(traceWorkers.Except(moduleMachines), Is.Not.Empty, "the indexed block is traced on the parallel tracer's workers");
        }
    }

    [Test]
    public async Task An_all_kinds_module_reaches_no_block_processing_or_witness_generation()
    {
        FrameOfferProbe probe = new();
        using TestRpcBlockchain chain = await Chain(probe, ReadOnlyQueryKind.All);

        // Block production and processing, then the witness pool shared with engine_newPayloadWithWitness.
        Block block = await AddBlockCallingEachContract(chain);
        int afterBlock = probe.Offers;
        using ResultWrapper<Witness> witness = chain.DebugRpcModule.debug_executionWitness(new BlockParameter(block.Number));
        int afterWitness = probe.Offers;
        using ResultWrapper<CallResultWithProof> proof = chain.ProofRpcModule.proof_call(
            new LegacyTransactionForRpc { From = TestItem.AddressB, To = Target, Gas = 100_000 }, new BlockParameter(block.Number));
        int afterProof = probe.Offers;
        string call = await chain.TestEthRpc("eth_call", CallTo(Target), "latest");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(witness.Result.ResultType, Is.EqualTo(ResultType.Success), witness.Result.Error);
            Assert.That(proof.Result.ResultType, Is.EqualTo(ResultType.Success), proof.Result.Error);
            Assert.That(proof.Data.Result, Is.EqualTo(Returning42Word()), "proof_call ran the contract");
            Assert.That(afterBlock, Is.Zero, "block production and processing must not run on a query module's runner");
            Assert.That(afterWitness, Is.Zero, "debug_executionWitness generates its witness without the runner");
            Assert.That(afterProof, Is.Zero, "proof_call generates its witness without the runner");
            AssertSucceeded(call);
            Assert.That(probe.Offers, Is.GreaterThan(0), "precondition: the module is loaded into the query environments");
        }
    }

    [Test]
    public async Task An_all_kinds_module_is_not_loaded_into_the_frame_transaction_prefix_simulator()
    {
        Address factory = new("0xfac0000000000000000000000000000000000001");
        byte[] salt = new byte[32];
        byte[] deployedCode = Prepare.EvmCode
            .PushData((byte)FrameFlags.ApproveExecutionAndPayment).PushData(0).PushData(0).Op(Instruction.APPROVE).Done;
        byte[] initCode = Prepare.EvmCode.ForInitOf(deployedCode).Done;
        Address deployed = ContractAddress.From(factory, salt, initCode);
        FrameOfferProbe probe = new();
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build(builder => builder
            .AddSingleton<ISpecProvider>(new TestSpecProvider(Eip8141Prototype.Instance))
            .AddSingleton<IReadOnlyQueryModule>(probe.ModuleFor(ReadOnlyQueryKind.All))
            // The first traced simulation runs cold and overruns the default timeout on CI.
            .AddSingleton<ITxPoolConfig>(new TxPoolConfig { FrameTxSimulationTimeoutMs = 0 })
            .WithGenesisPostProcessor((_, state, specProvider) =>
            {
                state.CreateAccount(factory, UInt256.Zero);
                state.InsertCode(factory, Prepare.EvmCode.Create2(initCode, salt, 0).Done, specProvider.GenesisSpec);
                state.CreateAccount(deployed, 10.Ether);
            }));

        // The validation prefix deploys the sender's code through the factory, whose APPROVE then names the payer. The
        // simulator builds its environment on first use; it traces every instruction, so frames alone would not show
        // the module there.
        int loadsBefore = probe.Loads;
        FrameTxSimulationResult result = chain.Container.Resolve<IFrameTxPrefixSimulator>().Simulate(new Transaction
        {
            Type = TxType.FrameTx,
            ChainId = TestBlockchainIds.ChainId,
            Nonce = 0,
            SenderAddress = deployed,
            Frames =
            [
                new TxFrame(FrameMode.Default, FrameFlags.None, factory, executionGasLimit: 200_000, stateGasLimit: 200_000, UInt256.Zero, default),
                new TxFrame(FrameMode.Verify, FrameFlags.ApproveExecutionAndPayment, target: null, gasLimit: 200_000, UInt256.Zero, default),
            ],
            FrameSignatures = [],
            GasPrice = 1.GWei,
            DecodedMaxFeePerGas = 1.GWei,
        }, local: true);
        int afterSimulation = probe.Offers;
        int loadsAfterSimulation = probe.Loads;
        string call = await chain.TestEthRpc("eth_call", CallTo(factory), "latest");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Payer, Is.EqualTo(deployed), result.Reason);
            Assert.That(afterSimulation, Is.Zero, "admitting a frame transaction to the pool must not run on a query module's runner");
            Assert.That(loadsAfterSimulation, Is.EqualTo(loadsBefore), "the prefix simulator's environment must not load query modules");
            AssertSucceeded(call);
            Assert.That(probe.Offers, Is.GreaterThan(0), "precondition: the module is loaded into the query environments");
        }
    }

    [Test]
    public async Task Traced_replays_with_a_module_persist_nothing()
    {
        FrameOfferProbe probe = new();
        using TestRpcBlockchain chain = await Chain(probe, ReadOnlyQueryKind.All);
        Block block = await AddBlockCallingEachContract(chain);
        BlockHeader head = chain.BlockTree.Head!.Header;
        // Traced at the sender's next nonce, then included in the next block.
        Transaction traced = SignedCallTo(chain, Target);
        List<BlockHeader> stored = [];
        void Stored(object? sender, ReceiptsEventArgs args) => stored.Add(args.BlockHeader);

        string[] responses;
        chain.ReceiptStorage.ReceiptsInserted += Stored;
        try
        {
            responses =
            [
                await Trace(chain, "debug_traceBlockByNumber", block, "callTracer"),
                await Trace(chain, "debug_traceTransaction", block, "callTracer"),
                await Trace(chain, "debug_traceCall", block, "callTracer"),
                await Trace(chain, "trace_replayBlockTransactions", block, "stateDiff"),
                await Trace(chain, "trace_call", block, "stateDiff"),
                await RpcTest.TestSerializedRequest(chain.TraceRpcModule, "trace_rawTransaction", Encoded(traced), new[] { "trace" }),
                await Simulate(chain, "eth_simulateV1"),
            ];
        }
        finally
        {
            chain.ReceiptStorage.ReceiptsInserted -= Stored;
        }

        Hash256? headAfterTraces = chain.BlockTree.Head!.Hash;
        Hash256? tracedTransactionBlock = chain.ReceiptFinder.FindBlockHash(traced.Hash!);
        UInt256[] countsAtHead = Contracts.Select(contract =>
        {
            chain.StateReader.GetStorage(head, contract, UInt256.Zero, out UInt256 count);
            return count;
        }).ToArray();

        // Each traced call to Target counted once more, in its own environment; the next block counts on the head's state.
        Block next = await chain.AddBlock(traced);
        chain.StateReader.GetStorage(next.Header, Target, UInt256.Zero, out UInt256 countAfterNext);

        using (Assert.EnterMultipleScope())
        {
            foreach (string response in responses) AssertSucceeded(response);
            Assert.That(probe.OffersFor(Target), Is.GreaterThan(0), "precondition: the traces ran with the runner");
            Assert.That(headAfterTraces, Is.EqualTo(head.Hash), "the head must not move");
            Assert.That(stored, Is.Empty, "a traced replay or call must store no receipts");
            Assert.That(tracedTransactionBlock, Is.Null, "the transaction traced by trace_rawTransaction must not be found in a block");
            Assert.That(countsAtHead, Has.All.EqualTo(UInt256.One), "the head's state must stay as its block left it");
            Assert.That(next.Transactions, Has.Length.EqualTo(1), "the nonce of the transaction traced by trace_rawTransaction must still be free");
            Assert.That(countAfterNext, Is.EqualTo((UInt256)2), "the next block must count on the head's state, not on what a traced call left");
        }
    }

    /// <summary>Lets the blocks it indexes be traced on the workers of the parallel tracers.</summary>
    private sealed class ParallelTracing : IDisposable
    {
        private readonly SnapshotableMemColumnsDb<FlatHistoryColumns> _columns = new();
        private readonly ParallelTraceBudget _budget = new(2);
        private readonly TransactionChangesetIndex _index;

        public ParallelTracing() => _index = new TransactionChangesetIndex(_columns, new FlatDbConfig { HistoryTransactionIndexEnabled = true });

        public void Configure(ContainerBuilder builder) => builder
            .AddSingleton<IPrefixStateSeedSource>(new ChangesetPrefixStateSeedSource(_index))
            .AddSingleton(_budget)
            .AddSingleton<ParallelTraceBudgets, ISpecProvider>(specProvider => new ParallelTraceBudgets(specProvider, _budget, changesetSeeds: true));

        /// <summary>Indexes <paramref name="block"/>, added on top of <paramref name="parent"/>.</summary>
        public void Index(TestRpcBlockchain chain, Block block, BlockHeader parent) => DebugRpcModuleTests.IndexThroughTheCapture(chain, _index, block, parent);

        public void Dispose()
        {
            _budget.Dispose();
            _columns.Dispose();
        }
    }

    /// <summary>
    /// A module that registers a spec provider of its own, passing everything through to the chain's, and records for each
    /// environment it is loaded into whether the environment resolves that one rather than a spec provider it registers itself.
    /// </summary>
    private sealed class SpecProviderReplacement(ISpecProvider specProvider) : Module, IReadOnlyQueryModule
    {
        private readonly OverridableSpecProvider _replacement = new(specProvider);
        private readonly ConcurrentQueue<LoadedEnvironment> _environments = new();

        public readonly record struct LoadedEnvironment(bool ResolvesTheReplacement, bool IsParallelTracerWorker);

        public ReadOnlyQueryKind AppliesTo => ReadOnlyQueryKind.All;

        public LoadedEnvironment[] Environments => _environments.ToArray();

        protected override void Load(ContainerBuilder builder) => builder
            .AddScoped<ISpecProvider>(_replacement)
            .OnBuild(scope => _environments.Enqueue(new LoadedEnvironment(
                ReferenceEquals(scope.Resolve<ISpecProvider>(), _replacement),
                scope.IsRegistered<ParallelBlockTracer.Components>())));
    }
}
