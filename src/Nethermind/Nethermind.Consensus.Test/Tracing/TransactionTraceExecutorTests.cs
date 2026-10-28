// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Tracing;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Tracing;

public class TransactionTraceExecutorTests
{
    [Test]
    public void CanSeed_probes_the_state_once_per_environment([Values] bool accepts)
    {
        CountingOverlayState state = new(accepts);
        TransactionTraceExecutor executor = Create(state, Substitute.For<IBlockAccessListManager>());

        bool[] answers = [executor.CanSeed, executor.CanSeed, executor.CanSeed];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(answers, Is.All.EqualTo(accepts));
            Assert.That(state.Probes, Is.EqualTo(1));
        }
    }

    [Test]
    public void CanSeed_follows_forced_access_list_construction_after_the_probe()
    {
        CountingOverlayState state = new(accepts: true);
        IBlockAccessListManager balManager = Substitute.For<IBlockAccessListManager>();
        TransactionTraceExecutor executor = Create(state, balManager);

        Assert.That(executor.CanSeed, Is.True);
        balManager.ForceConstructGeneratedBlockAccessList.Returns(true);
        Assert.That(executor.CanSeed, Is.False);
        balManager.ForceConstructGeneratedBlockAccessList.Returns(false);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(executor.CanSeed, Is.True);
            Assert.That(state.Probes, Is.EqualTo(1));
        }
    }

    [Test]
    public void CanSeed_without_an_overlay_slot_does_not_probe()
    {
        CountingOverlayState state = new(accepts: true);
        TransactionTraceExecutor executor = new(
            Substitute.For<IBlockProcessor.IBlockTransactionsExecutor>(),
            Substitute.For<ITransactionProcessorAdapter>(),
            state,
            Substitute.For<IBlockAccessListManager>(),
            CreateSenderRecovery());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(executor.CanSeed, Is.False);
            Assert.That(state.Probes, Is.Zero);
        }
    }

    private static TransactionTraceExecutor Create(IWorldState state, IBlockAccessListManager balManager) => new(
        Substitute.For<IBlockProcessor.IBlockTransactionsExecutor>(),
        Substitute.For<ITransactionProcessorAdapter>(),
        state,
        balManager,
        CreateSenderRecovery(),
        readOverlay: new StateReadOverlaySlot());

    private static RecoverSignatures CreateSenderRecovery() =>
        new(Substitute.For<IEthereumEcdsa>(), Substitute.For<ISpecProvider>(), LimboLogs.Instance);

    private sealed class CountingOverlayState(bool accepts) : WorldStateDecorator(Substitute.For<IWorldState>())
    {
        public int Probes { get; private set; }

        public override bool TryApplyAccountOverlay(IStateReadOverlay overlay)
        {
            Probes++;
            return accepts;
        }
    }
}
