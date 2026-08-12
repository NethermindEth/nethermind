// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System.Threading;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Processing;

/// <summary>
/// The admission bounds <see cref="FrameTxPrefixSimulator"/> applies before any EVM work: the frame-tx
/// boundary guard and the per-head simulation time budget.
/// </summary>
public class FrameTxPrefixSimulatorTests
{
    [Test]
    public void Simulate_NonFrameTransaction_RejectedWithoutBuildingAnEnv()
    {
        IReadOnlyTxProcessingEnvFactory envFactory = Substitute.For<IReadOnlyTxProcessingEnvFactory>();
        using FrameTxPrefixSimulator simulator = Create(envFactory, out _);

        FrameTxSimulationResult result = simulator.Simulate(Build.A.Transaction.TestObject);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Accepted, Is.False);
            Assert.That(result.Indeterminate, Is.False, "a wrong transaction type is a definite rejection");
            envFactory.DidNotReceive().Create();
        }
    }

    [Test]
    public void Simulate_WithoutAChainHead_IsIndeterminate()
    {
        IReadOnlyTxProcessingEnvFactory envFactory = Substitute.For<IReadOnlyTxProcessingEnvFactory>();
        using FrameTxPrefixSimulator simulator = Create(envFactory, out IBlockFinder blockFinder);
        blockFinder.Head.Returns((Block?)null);

        FrameTxSimulationResult result = simulator.Simulate(FrameTx());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Accepted, Is.False);
            Assert.That(result.Indeterminate, Is.True, "nothing was learned about the prefix");
        }
    }

    [Test]
    public void Simulate_ExhaustedHeadBudget_IsIndeterminateAndSpendsNoFurtherWork()
    {
        IReadOnlyTxProcessingEnvFactory envFactory = Substitute.For<IReadOnlyTxProcessingEnvFactory>();
        using FrameTxPrefixSimulator simulator = Create(envFactory, out _, budgetPerHeadMs: 1);
        // The first simulation overruns the 1 ms budget, so the second finds none left for this head.
        simulator.Simulate(FrameTx());
        envFactory.ClearReceivedCalls();

        FrameTxSimulationResult result = simulator.Simulate(FrameTx());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Accepted, Is.False);
            Assert.That(result.Indeterminate, Is.True);
            Assert.That(result.RejectionReason, Does.Contain("budget"));
            envFactory.DidNotReceive().Create();
        }
    }

    [Test]
    public void Simulate_CancelledBeforeEntry_Throws()
    {
        using FrameTxPrefixSimulator simulator = Create(Substitute.For<IReadOnlyTxProcessingEnvFactory>(), out _);
        using CancellationTokenSource cts = new();
        cts.Cancel();

        Assert.Throws<System.OperationCanceledException>(() => simulator.Simulate(FrameTx(), cts.Token));
    }

    private static FrameTxPrefixSimulator Create(
        IReadOnlyTxProcessingEnvFactory envFactory,
        out IBlockFinder blockFinder,
        int budgetPerHeadMs = 1000)
    {
        blockFinder = Substitute.For<IBlockFinder>();
        blockFinder.Head.Returns(Build.A.Block.WithNumber(1).TestObject);
        // Standing in for a simulation that got past the bounds: it must be observable that it ran, and
        // it must consume measurable time so the per-head budget can be driven deterministically.
        envFactory.Create().Returns(_ =>
        {
            Thread.Sleep(5);
            throw new TestEnvUnavailableException();
        });
        return new FrameTxPrefixSimulator(
            envFactory,
            blockFinder,
            new TestSpecProvider(Bogota.Instance),
            new TxPoolConfig { FrameTxSimulationBudgetPerHeadMs = budgetPerHeadMs },
            LimboLogs.Instance);
    }

    private static Transaction FrameTx() => new()
    {
        Type = TxType.FrameTx,
        SenderAddress = TestItem.AddressA,
        Frames = [new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, gasLimit: 50_000, UInt256.Zero, default)],
        FrameSignatures = [],
        DecodedMaxFeePerGas = UInt256.One,
    };

    private sealed class TestEnvUnavailableException : System.Exception;
}
