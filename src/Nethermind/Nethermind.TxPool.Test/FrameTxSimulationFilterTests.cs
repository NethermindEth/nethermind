// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.TxPool.Filters;
using NSubstitute;
using NUnit.Framework;

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

    [Test]
    public void Accept_OpaquePrefix_SimulatesAndRecordsResolvedPayer()
    {
        TestReadOnlyStateProvider state = DeployedCodeSenderState();
        Transaction tx = SelfVerifyTx(TestItem.AddressA);
        IFrameTxPrefixSimulator simulator = Substitute.For<IFrameTxPrefixSimulator>();
        simulator.Simulate(tx).Returns(FrameTxSimulationResult.Accept(TestItem.AddressB));

        AcceptTxResult result = Accept(state, simulator, tx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(tx.PayerAddress, Is.EqualTo(TestItem.AddressB));
            simulator.Received(1).Simulate(tx);
        }
    }

    [Test]
    public void Accept_OpaquePrefixFailsSimulation_Rejected()
    {
        TestReadOnlyStateProvider state = DeployedCodeSenderState();
        Transaction tx = SelfVerifyTx(TestItem.AddressA);
        IFrameTxPrefixSimulator simulator = Substitute.For<IFrameTxPrefixSimulator>();
        simulator.Simulate(tx).Returns(FrameTxSimulationResult.Reject("banned opcode"));

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
        simulator.Simulate(tx).Returns(FrameTxSimulationResult.Undecided("simulation unavailable"));

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

    private static TestReadOnlyStateProvider DeployedCodeSenderState()
    {
        TestReadOnlyStateProvider state = new();
        state.CreateAccount(TestItem.AddressA, 1 * Unit.Ether);
        state.InsertCode([0x00], TestItem.AddressA); // deployed code ⇒ RequiresSimulation
        return state;
    }

    private static Transaction SelfVerifyTx(Address sender) => new()
    {
        Type = TxType.FrameTx,
        SenderAddress = sender,
        Frames = [new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, gasLimit: 100_000, UInt256.Zero, default)],
        FrameSignatures = [new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, sender, default, new byte[TxFrameSignature.Secp256k1SignatureLength])],
    };

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
