// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Int256;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

/// <summary>Read-only query modules configure the environments serving queries, and never block processing.</summary>
[TestFixture]
[Parallelizable(ParallelScope.Self)]
public class ReadOnlyQueryModuleTests
{
    private static readonly Address OverrideTarget = new("0xc2000000000000000000000000000000000000c1");

    /// <summary>Declines every frame it is offered, counting the offers.</summary>
    private sealed class CountingRunner : FrameCodeRunner<EthereumGasPolicy>
    {
        private int _offers;

        public int Offers => Volatile.Read(ref _offers);

        protected override EvmExceptionType Run(VirtualMachine<EthereumGasPolicy> vm, CodeInfo codeInfo, OpcodeBodySet bodies,
            scoped ref EvmStack stack, scoped ref EthereumGasPolicy gas, ref nint programCounter, ref int opCodesSincePoll, FrameRunFlags flags)
        {
            Interlocked.Increment(ref _offers);
            return Declined;
        }
    }

    /// <summary>Attaches the runner to the machines of the query environments it applies to.</summary>
    private sealed class ProbeModule(CountingRunner runner, ReadOnlyQueryKind appliesTo) : Module, IReadOnlyQueryModule
    {
        public ReadOnlyQueryKind AppliesTo => appliesTo;

        protected override void Load(ContainerBuilder builder) => builder.RegisterInstance(runner).As<FrameCodeRunner<EthereumGasPolicy>>();
    }

    private static byte[] Returning42() => Prepare.EvmCode.PushData(42).PushData(0).Op(Instruction.MSTORE).Return(32, 0).Done;

    private static Task<TestRpcBlockchain> Chain(CountingRunner runner, ReadOnlyQueryKind appliesTo) =>
        TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .Build(builder => builder.AddSingleton<IReadOnlyQueryModule>(new ProbeModule(runner, appliesTo)));

    private static LegacyTransactionForRpc Query(TestRpcBlockchain chain, Address to) =>
        new(Build.A.Transaction.WithTo(to).WithGasLimit(100_000).WithGasPrice(0).SignedAndResolved(TestItem.PrivateKeyB).TestObject,
            new(chain.SpecProvider.ChainId));

    [Test]
    public async Task Calls_offer_frames_and_block_processing_never_does()
    {
        CountingRunner runner = new();
        using TestRpcBlockchain chain = await Chain(runner, ReadOnlyQueryKind.Call);

        // Deploying a contract runs its init code in block production and processing.
        UInt256 nonce = chain.ReadOnlyState.GetNonce(TestItem.AddressA);
        Transaction deploy = Build.A.Transaction.WithCode(Prepare.EvmCode.ForInitOf(Returning42()).Done).WithGasLimit(1_000_000)
            .WithNonce((ulong)nonce).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        await chain.AddBlock(deploy);
        Address deployed = ContractAddress.From(TestItem.AddressA, nonce);
        Assert.That(runner.Offers, Is.Zero, "block production and processing must not run on a query module's runner");

        string call = await chain.TestEthRpc("eth_call", Query(chain, deployed), "latest");
        int afterCall = runner.Offers;
        Dictionary<Address, AccountOverride> overrides = new() { [OverrideTarget] = new() { Code = Returning42() } };
        string overridden = await chain.TestEthRpc("eth_call", Query(chain, OverrideTarget), "latest", overrides);
        int afterOverride = runner.Offers;
        string estimate = await chain.TestEthRpc("eth_estimateGas", Query(chain, deployed), "latest");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(JToken.Parse(call)["result"]?.Value<string>(), Does.EndWith("2a"), call);
            Assert.That(JToken.Parse(overridden)["result"]?.Value<string>(), Does.EndWith("2a"), overridden);
            Assert.That(JToken.Parse(estimate)["result"], Is.Not.Null, estimate);
            Assert.That(afterCall, Is.GreaterThan(0), "eth_call runs in the shared read-only environments");
            Assert.That(afterOverride, Is.GreaterThan(afterCall), "a call with a state override runs in the per-call environment");
            Assert.That(runner.Offers, Is.GreaterThan(afterOverride), "eth_estimateGas runs in the shared read-only environments");
        }
    }

    [Test]
    public async Task A_module_for_no_query_kind_is_not_loaded_for_calls()
    {
        CountingRunner runner = new();
        using TestRpcBlockchain chain = await Chain(runner, ReadOnlyQueryKind.None);
        Dictionary<Address, AccountOverride> overrides = new() { [OverrideTarget] = new() { Code = Returning42() } };

        string overridden = await chain.TestEthRpc("eth_call", Query(chain, OverrideTarget), "latest", overrides);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(JToken.Parse(overridden)["result"]?.Value<string>(), Does.EndWith("2a"), overridden);
            Assert.That(runner.Offers, Is.Zero, "a module applies only to the query kinds it names");
        }
    }
}
