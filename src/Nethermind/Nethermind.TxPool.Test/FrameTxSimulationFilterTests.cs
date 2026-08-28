// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.TxPool.Filters;
using NSubstitute;
using NUnit.Framework;
using static Nethermind.Core.Test.Builders.FrameTxTestFrames;

namespace Nethermind.TxPool.Test;

public class FrameTxSimulationFilterTests
{
    [Test]
    public void Accept_NativelyResolvedFastPath_DoesNotInvokeSimulator()
    {
        TestReadOnlyStateProvider state = new();
        state.CreateAccount(TestItem.AddressA, 1 * Unit.Ether); // default-code sender ⇒ legible
        Transaction tx = SelfVerifyTx(TestItem.AddressA);
        IFrameTxPrefixSimulator simulator = Substitute.For<IFrameTxPrefixSimulator>();

        // The payer filter resolves the legible prefix natively, then the simulation filter runs.
        RunPayerFilter(state, tx);
        AcceptTxResult result = Accept(state, simulator, tx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(tx.PayerAddress, Is.EqualTo(TestItem.AddressA));
            simulator.DidNotReceiveWithAnyArgs().Simulate(default!);
        }
    }

    [Test]
    public void Accept_NonFrameTx_DoesNotInvokeSimulator()
    {
        Transaction tx = Build.A.Transaction.WithSenderAddress(TestItem.AddressA).TestObject;
        IFrameTxPrefixSimulator simulator = Substitute.For<IFrameTxPrefixSimulator>();

        AcceptTxResult result = Accept(new TestReadOnlyStateProvider(), simulator, tx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
            simulator.DidNotReceiveWithAnyArgs().Simulate(default!);
        }
    }

    // The pre-validated assertion has to come from what actually ran: a filter chain that has not
    // verified the signatures must make the simulation verify them rather than trust a stranger's.
    [TestCase(true, TestName = "Verified signatures are not re-verified by the simulation")]
    [TestCase(false, TestName = "Unverified signatures are re-verified by the simulation")]
    public void Accept_OpaquePrefix_SimulatesAndRecordsResolvedPayer(bool signaturesVerified)
    {
        TestReadOnlyStateProvider state = DeployedCodeSenderState();
        Transaction tx = SelfVerifyTx(TestItem.AddressA);
        IFrameTxPrefixSimulator simulator = Substitute.For<IFrameTxPrefixSimulator>();
        simulator.Simulate(tx, Arg.Any<bool>()).Returns(FrameTxSimulationResult.Accept(TestItem.AddressB));

        AcceptTxResult result = Accept(state, simulator, tx, signaturesVerified);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(tx.PayerAddress, Is.EqualTo(TestItem.AddressB));
            simulator.Received(1).Simulate(tx, signaturesPreValidated: signaturesVerified);
        }
    }

    [Test]
    public void Accept_OpaquePrefixFailsSimulation_Rejected()
    {
        TestReadOnlyStateProvider state = DeployedCodeSenderState();
        Transaction tx = SelfVerifyTx(TestItem.AddressA);
        IFrameTxPrefixSimulator simulator = Substitute.For<IFrameTxPrefixSimulator>();
        simulator.Simulate(tx, Arg.Any<bool>()).Returns(FrameTxSimulationResult.Reject("banned opcode"));

        AcceptTxResult result = Accept(state, simulator, tx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(AcceptTxResult.FrameSimulationFailed));
            Assert.That(tx.PayerAddress, Is.Null);
        }
    }

    [Test]
    public void Accept_OpaquePrefixUndecidedBySimulator_DefersInsteadOfChargingTheSender()
    {
        // A node-side fault must not produce a non-accepting result, which the peer's flood counter
        // would charge and eventually disconnect over.
        TestReadOnlyStateProvider state = DeployedCodeSenderState();
        Transaction tx = SelfVerifyTx(TestItem.AddressA);
        IFrameTxPrefixSimulator simulator = Substitute.For<IFrameTxPrefixSimulator>();
        simulator.Simulate(tx, Arg.Any<bool>()).Returns(FrameTxSimulationResult.Undecided("simulation unavailable"));

        AcceptTxResult result = Accept(state, simulator, tx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(tx.PayerAddress, Is.Null);
        }
    }

    [Test]
    public void Accept_OpaquePrefixWithoutSimulator_DefersLikePhase1()
    {
        TestReadOnlyStateProvider state = DeployedCodeSenderState();
        Transaction tx = SelfVerifyTx(TestItem.AddressA);

        AcceptTxResult result = Accept(state, simulator: null, tx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(tx.PayerAddress, Is.Null);
        }
    }

    // An unset result carries no payer, so it must not read as Accepted and have that null recorded.
    [Test]
    public void DefaultSimulationResult_IsUndecided() =>
        Assert.That(default(FrameTxSimulationResult).Outcome, Is.EqualTo(FrameTxSimulationOutcome.Undecided));

    private static TestReadOnlyStateProvider DeployedCodeSenderState()
    {
        TestReadOnlyStateProvider state = new();
        state.CreateAccount(TestItem.AddressA, 1 * Unit.Ether);
        state.InsertCode([0x00], TestItem.AddressA); // deployed code ⇒ RequiresSimulation
        return state;
    }

    private static Transaction SelfVerifyTx(Address sender) =>
        FrameTx(sender, [Secp256k1Signature(sender)], SelfVerify(PrefixFrameGas));

    private static void RunPayerFilter(TestReadOnlyStateProvider state, Transaction tx)
    {
        FrameTxPayerFilter filter = new(state, LimboLogs.Instance.GetClassLogger<FrameTxSimulationFilterTests>());
        TxFilteringState filteringState = new(tx, state);
        filter.Accept(tx, ref filteringState, TxHandlingOptions.None);
    }

    [Test]
    public void Accept_SimulationDeferredByAdmissionBound_IsDistinctFromRejection()
    {
        // Peer scoring must be able to tell this node's load shedding from a peer sending bad transactions.
        TestReadOnlyStateProvider state = DeployedCodeSenderState();
        Transaction tx = SelfVerifyTx(TestItem.AddressA);
        IFrameTxPrefixSimulator simulator = Substitute.For<IFrameTxPrefixSimulator>();
        simulator.Simulate(tx, Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<bool>()).Returns(FrameTxSimulationResult.RejectIndeterminate("budget exhausted"));

        AcceptTxResult result = Accept(state, simulator, tx);

        Assert.That(result, Is.EqualTo(AcceptTxResult.FrameSimulationDeferred));
    }

    [Test]
    public void Accept_SimulationTimedOut_IsChargedToTheSender()
    {
        // The prefix's own wall clock trips the timeout, so the peer chose it: retained by revalidation,
        // but it must still count against the sender rather than reading as this node shedding load.
        TestReadOnlyStateProvider state = DeployedCodeSenderState();
        Transaction tx = SelfVerifyTx(TestItem.AddressA);
        IFrameTxPrefixSimulator simulator = Substitute.For<IFrameTxPrefixSimulator>();
        simulator.Simulate(tx, Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<bool>()).Returns(FrameTxSimulationResult.RejectTimedOut("timed out"));

        AcceptTxResult result = Accept(state, simulator, tx);

        Assert.That(result, Is.EqualTo(AcceptTxResult.FrameSimulationFailed));
    }

    private static AcceptTxResult Accept(TestReadOnlyStateProvider state, IFrameTxPrefixSimulator? simulator, Transaction tx, bool signaturesVerified = false)
    {
        FrameTxSimulationFilter filter = new(state, simulator, LimboLogs.Instance.GetClassLogger<FrameTxSimulationFilterTests>());
        TxFilteringState filteringState = new(tx, state) { FrameSignaturesVerified = signaturesVerified };
        return filter.Accept(tx, ref filteringState, TxHandlingOptions.None);
    }
}
