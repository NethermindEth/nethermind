// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System.Collections.Generic;
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

    // An undecided verdict is a node-side fault; it must not produce a non-accepting result, which the
    // peer's flood counter would charge and eventually disconnect over.
    [TestCaseSource(nameof(OpaquePrefixCases))]
    public void Accept_OpaquePrefix_FollowsTheSimulatorsVerdict(FrameTxSimulationResult simulation, AcceptTxResult expected, Address? expectedPayer)
    {
        TestReadOnlyStateProvider state = DeployedCodeSenderState();
        Transaction tx = SelfVerifyTx(TestItem.AddressA);
        IFrameTxPrefixSimulator simulator = Substitute.For<IFrameTxPrefixSimulator>();
        simulator.Simulate(tx).Returns(simulation);

        AcceptTxResult result = Accept(state, simulator, tx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(expected));
            Assert.That(tx.PayerAddress, Is.EqualTo(expectedPayer));
            simulator.Received(1).Simulate(tx);
        }
    }

    public static IEnumerable<TestCaseData> OpaquePrefixCases()
    {
        yield return new TestCaseData(FrameTxSimulationResult.Accept(TestItem.AddressB), AcceptTxResult.Accepted, TestItem.AddressB)
            .SetName("Accept_OpaquePrefix_SimulatesAndRecordsResolvedPayer");
        yield return new TestCaseData(FrameTxSimulationResult.Reject("banned opcode"), AcceptTxResult.FrameSimulationFailed, null)
            .SetName("Accept_OpaquePrefixFailsSimulation_Rejected");
        yield return new TestCaseData(FrameTxSimulationResult.Undecided("simulation unavailable"), AcceptTxResult.Accepted, null)
            .SetName("Accept_OpaquePrefixUndecidedBySimulator_DefersInsteadOfChargingTheSender");
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
        FrameTx(sender, [Secp256k1Signature(sender)], SelfVerify(gasLimit: 100_000));

    private static void RunPayerFilter(TestReadOnlyStateProvider state, Transaction tx)
    {
        FrameTxPayerFilter filter = new(state, LimboLogs.Instance.GetClassLogger<FrameTxSimulationFilterTests>());
        TxFilteringState filteringState = new(tx, state);
        filter.Accept(tx, ref filteringState, TxHandlingOptions.None);
    }

    private static AcceptTxResult Accept(TestReadOnlyStateProvider state, IFrameTxPrefixSimulator? simulator, Transaction tx)
    {
        FrameTxSimulationFilter filter = new(state, simulator, LimboLogs.Instance.GetClassLogger<FrameTxSimulationFilterTests>());
        TxFilteringState filteringState = new(tx, state);
        return filter.Accept(tx, ref filteringState, TxHandlingOptions.None);
    }
}
