// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Reflection;
using Autofac;
using Autofac.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Eez.Execution;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.LightClient.Rpc;
using Nethermind.Logging;
using NSubstitute;

namespace Nethermind.LightClient.Test;

public class EezExecutionRulesTests
{
    private const string Bootnode = "enode://81863f47e9bd652585d3f78b4b2ee07b93dad603fd9bc3c293e1244250725998adc88da0cef48f1de89b15ab92b15db8f43dc2b6fb8fbd86a6f217a1dd886701@193.70.55.37:30303";
    private static readonly string Genesis = Path.Combine(AppContext.BaseDirectory, "TestData", "Eez", "rollupx-eezdev-699069-genesis.json");

    [Test]
    public void Rollupx_genesis_loads_rollupx_rules() =>
        Assert.That(EezExecutionRules.FromGenesis(Genesis).Rules.IsRollupX, Is.True);

    [Test]
    public async Task Rollup_transport_uses_eez_spec_provider()
    {
        EezExecutionRules rules = EezExecutionRules.FromGenesis(Genesis);
        ExecutionNetwork network = ExecutionNetwork.Custom("rollupx", Genesis, Keccak.Compute("rollup genesis"), null, Bootnode)
            with { ExecutionRules = rules };
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
        EezExecutionRules rules = EezExecutionRules.FromGenesis(Genesis);
        using VerifiedCall rollupCalls = new(null!, Substitute.For<ISpecProvider>(), LimboLogs.Instance,
            executionRules: rules.ForCalls());
        using VerifiedCall ethereumCalls = new(null!, Substitute.For<ISpecProvider>(), LimboLogs.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ProcessorType(rollupCalls), Is.EqualTo(typeof(EezTransactionProcessor)));
            Assert.That(ProcessorType(ethereumCalls), Is.Not.EqualTo(typeof(EezTransactionProcessor)));
        }
    }

    private static Type ProcessorType(VerifiedCall calls)
    {
        IContainer container = (IContainer)typeof(VerifiedCall)
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
