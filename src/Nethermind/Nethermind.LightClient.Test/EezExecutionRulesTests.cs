// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Reflection;
using System.Text.Json;
using Autofac;
using Autofac.Core;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.LightClient.Eez;
using Nethermind.LightClient.Rpc;
using Nethermind.Logging;
using NSubstitute;

namespace Nethermind.LightClient.Test;

[NonParallelizable]
public class EezExecutionRulesTests
{
    private const string Bootnode = "enode://81863f47e9bd652585d3f78b4b2ee07b93dad603fd9bc3c293e1244250725998adc88da0cef48f1de89b15ab92b15db8f43dc2b6fb8fbd86a6f217a1dd886701@193.70.55.37:30303";
    private static readonly string Genesis = Path.Combine(AppContext.BaseDirectory, "TestData", "Eez", "rollupx-eezdev-699069-genesis.json");

    [Test]
    public void Rollupx_genesis_loads_rollupx_rules() =>
        Assert.That(EezExecutionRules.FromGenesis(Genesis, 10_200).Rules.IsRollupX, Is.True);

    [Test]
    public void FromGenesis_WhenSettlementChainDoesNotMatch_RejectsConfiguration() =>
        Assert.That(() => EezExecutionRules.FromGenesis(Genesis, 1), Throws.ArgumentException);

    [Test]
    public async Task Rollup_transport_uses_eez_spec_provider()
    {
        EezExecutionRules rules = EezExecutionRules.FromGenesis(Genesis, 10_200);
        ExecutionNetwork network = ExecutionNetwork.Custom("rollupx", Genesis, Keccak.Compute("rollup genesis"), null, Bootnode) with
        {
            ExecutionRules = rules
        };
        await using ExecutionPeerTransport transport = new(network, LimboLogs.Instance,
            new VerifiedHead(0, 1, Keccak.Compute("rollup head"), Keccak.Zero), 0, TestDirectory());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(transport.SpecProvider, Is.TypeOf<EezSpecProvider>());
            Assert.That(transport.SpecProvider.ChainId, Is.EqualTo(699_069UL));
        }
    }

    [Test]
    public void Rollup_calls_use_eez_transaction_processor()
    {
        EezExecutionRules rules = EezExecutionRules.FromGenesis(Genesis, 10_200);
        using EvmExecutor rollupCalls = new(null!, Substitute.For<ISpecProvider>(), LimboLogs.Instance,
            executionRules: rules.ForCalls());
        using EvmExecutor ethereumCalls = new(null!, Substitute.For<ISpecProvider>(), LimboLogs.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ProcessorType(rollupCalls), Is.EqualTo(typeof(EezTransactionProcessor)));
            Assert.That(ProcessorType(ethereumCalls), Is.Not.EqualTo(typeof(EezTransactionProcessor)));
            Assert.That(ethereumCalls.SupportsBlockExecution, Is.True);
        }
    }

    [Test]
    public void RegisterRpcTransactionTypes_NativeHistory_SerializesEnvelope([Values] bool explicitBlocks)
    {
        RollupXSchedule schedule = new(2, 6, new(1, 1, Keccak.Zero, 12, Keccak.Zero), 12);
        EezChainRules rules = EezChainRules.NativeRollupX(schedule, Address.FromNumber(1),
            explicitBlocks ? NativeSystemTxFormat.ContractV7 : NativeSystemTxFormat.ContractV4);
        EezExecutionRules.RegisterRpcTransactionTypes(rules);
        Hash256 hash = Keccak.Compute("history block");
        NativeHistoryEnvelope envelope = explicitBlocks
            ? NativeHistoryEnvelope.CreateBlocks(901, [new(32, hash.ValueHash256)])
            : NativeHistoryEnvelope.Create(901, [hash.ValueHash256]);

        TransactionForRpc rpc = TransactionForRpc.FromTransaction(new NativeHistoryTransaction(envelope));
        JsonElement root = BoundedJson.ToElement(rpc);
        JsonElement record = root.GetProperty(explicitBlocks ? "blocks" : "hashes")[0];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(root.GetProperty("type").GetString(), Is.EqualTo("0x46"));
            Assert.That(root.GetProperty("version").GetString(), Is.EqualTo("0x1"));
            Assert.That(root.GetProperty("from").GetString(), Is.EqualTo(Address.SystemUser.ToString()));
            Assert.That(root.TryGetProperty(explicitBlocks ? "hashes" : "blocks", out _), Is.False);
            Assert.That((explicitBlocks ? record.GetProperty("blockHash") : record).GetString(), Is.EqualTo(hash.ToString()));
            if (explicitBlocks)
                Assert.That(record.GetProperty("number").GetString(), Is.EqualTo("0x20"));
            Assert.That(rpc.ToTransaction().IsError, Is.True);
        }
    }

    [Test]
    public async Task Rollup_rpc_rejects_block_execution_without_rollup_block_rules([Values] bool simulation)
    {
        EezExecutionRules rules = EezExecutionRules.FromGenesis(Genesis, 10_200);
        IExecutionStateSource execution = Substitute.For<IExecutionStateSource>();
        using EvmExecutor calls = new(execution, Substitute.For<ISpecProvider>(), LimboLogs.Instance, executionRules: rules.ForCalls());
        using RpcBackend rpc = new(execution, () => throw new AssertionException("The RPC must reject before looking up a head."), 699_069, calls);

        RpcException? error = await Assert.ThrowsAsync<RpcException>(async () =>
        {
            if (simulation)
                await rpc.SimulateAsync(default, default, CancellationToken.None);
            else
                await rpc.TraceTransactionAsync(Keccak.Zero, null, CancellationToken.None);
        });
        Assert.That(error!.Code, Is.EqualTo(-32601));
    }

    private static Type ProcessorType(EvmExecutor calls)
    {
        IContainer container = (IContainer)typeof(EvmExecutor)
            .GetField("_processing", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(calls)!;
        container.ComponentRegistry.TryGetRegistration(new TypedService(typeof(ITransactionProcessor)), out IComponentRegistration? registration);
        return registration!.Activator.LimitType;
    }

    private static string TestDirectory()
    {
        string directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "artifacts", "NethermindLightClientEezTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
