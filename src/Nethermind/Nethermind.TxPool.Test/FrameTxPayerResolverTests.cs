// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.TxPool.Test;

public class FrameTxPayerResolverTests
{
    private static readonly Address Sender = TestItem.AddressA;
    private static readonly Address Sponsor = TestItem.AddressB;

    // expectedOutcome is boxed FrameTxPayerOutcome; the enum is internal so it cannot appear in this public signature.
    [TestCaseSource(nameof(OutcomeCases))]
    public void Resolve_OutcomeMatrix(Func<TestReadOnlyStateProvider, Transaction> build, object expectedOutcome, Address? expectedPayer)
    {
        TestReadOnlyStateProvider state = new();
        Transaction tx = build(state);

        FrameTxPayerResolution resolution = Resolve(tx, state);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resolution.Outcome, Is.EqualTo(expectedOutcome));
            Assert.That(resolution.Payer, Is.EqualTo(expectedPayer));
        }
    }

    private static FrameTxPayerResolution Resolve(Transaction tx, TestReadOnlyStateProvider state)
    {
        state.TryGetAccount(tx.SenderAddress!, out AccountStruct senderAccount);
        return FrameTxPayerResolver.Resolve(tx, state, senderAccount);
    }

    private static IEnumerable<TestCaseData> OutcomeCases()
    {
        yield return Case("SelfVerify_DefaultCodeSender_PayerIsSender",
            state =>
            {
                DefaultCodeAccount(state, Sender);
                return FrameTx([SelfVerifyFrame()], [Secp(Sender)]);
            }, FrameTxPayerOutcome.Resolved, Sender);

        // The sponsor's pay-frame signature is unverified at admission, so a third party is never named natively.
        yield return Case("OnlyVerifyPay_DefaultCodeSponsor_RequiresSimulation",
            state =>
            {
                DefaultCodeAccount(state, Sender);
                DefaultCodeAccount(state, Sponsor);
                return FrameTx([OnlyVerifyFrame(), PayFrame(Sponsor)], [Secp(Sender), Secp(Sponsor)]);
            }, FrameTxPayerOutcome.RequiresSimulation, null);

        // A following pay frame can name a sponsor if the sender's balance drops below max cost, so the
        // sender is not resolved natively — defer to simulation.
        yield return Case("SelfVerifyThenPay_RequiresSimulation",
            state =>
            {
                DefaultCodeAccount(state, Sender);
                DefaultCodeAccount(state, Sponsor);
                return FrameTx([SelfVerifyFrame(), PayFrame(Sponsor)], [Secp(Sender), Secp(Sponsor)]);
            }, FrameTxPayerOutcome.RequiresSimulation, null);

        // A never-seen sender still resolves: the zeroed account reads as default (empty) code.
        yield return Case("SelfVerify_NonExistentSender_PayerIsSender",
            _ => FrameTx([SelfVerifyFrame()], [Secp(Sender)]),
            FrameTxPayerOutcome.Resolved, Sender);

        yield return Case("OnlyVerifyWithoutPay_NoPayer",
            state =>
            {
                DefaultCodeAccount(state, Sender);
                return FrameTx([OnlyVerifyFrame()], [Secp(Sender)]);
            }, FrameTxPayerOutcome.NoPayer, null);

        // NoPayer is code-independent: even a deployed sender cannot approve payment on a prefix with no
        // following pay frame.
        yield return Case("OnlyVerifyWithoutPay_DeployedCodeSender_NoPayer",
            state =>
            {
                DeployedCodeAccount(state, Sender);
                return FrameTx([OnlyVerifyFrame()], [Secp(Sender)]);
            }, FrameTxPayerOutcome.NoPayer, null);

        // A leading deploy frame is part of the recognized prefix, so it is skipped like the expiry frame.
        yield return Case("DeployThenSelfVerify_PayerIsSender",
            state =>
            {
                DefaultCodeAccount(state, Sender);
                return FrameTx([DeployFrame(), SelfVerifyFrame()], [Secp(Sender)]);
            }, FrameTxPayerOutcome.Resolved, Sender);

        yield return Case("DeployFrameOnly_NoPayer",
            state =>
            {
                DefaultCodeAccount(state, Sender);
                return FrameTx([DeployFrame()], [Secp(Sender)]);
            }, FrameTxPayerOutcome.NoPayer, null);

        yield return Case("SelfVerify_DeployedCodeSender_RequiresSimulation",
            state =>
            {
                DeployedCodeAccount(state, Sender);
                return FrameTx([SelfVerifyFrame()], [Secp(Sender)]);
            }, FrameTxPayerOutcome.RequiresSimulation, null);

        yield return Case("OnlyVerifyPay_DeployedCodePaymaster_RequiresSimulation",
            state =>
            {
                DefaultCodeAccount(state, Sender);
                DeployedCodeAccount(state, Sponsor);
                return FrameTx([OnlyVerifyFrame(), PayFrame(Sponsor)], [Secp(Sender), Secp(Sponsor)]);
            }, FrameTxPayerOutcome.RequiresSimulation, null);

        // A wrong signature shape isn't proof of invalidity (its placement is unsettled), so it's deferred, not dropped.
        yield return Case("SelfVerify_WrongSignatureScheme_RequiresSimulation",
            state =>
            {
                DefaultCodeAccount(state, Sender);
                return FrameTx([SelfVerifyFrame()], [new TxFrameSignature(TxFrameSignature.SchemeP256, Sender, default, new byte[128])]);
            }, FrameTxPayerOutcome.RequiresSimulation, null);

        // An empty top-level signature list is deferred, not dropped: where the signature belongs is unsettled.
        yield return Case("SelfVerify_EmptySignatureList_RequiresSimulation",
            state =>
            {
                DefaultCodeAccount(state, Sender);
                return FrameTx([SelfVerifyFrame()], []);
            }, FrameTxPayerOutcome.RequiresSimulation, null);

        yield return Case("OnlyVerifyPay_SponsorSignatureShape_RequiresSimulation",
            state =>
            {
                DefaultCodeAccount(state, Sender);
                DefaultCodeAccount(state, Sponsor);
                return FrameTx([OnlyVerifyFrame(), PayFrame(Sponsor)], [Secp(Sender), Secp(Sender)]);
            }, FrameTxPayerOutcome.RequiresSimulation, null);

        // A leading expiry_verify frame is skipped for shape matching; the self relay still resolves.
        yield return Case("ExpiryThenSelfVerify_PayerIsSender",
            state =>
            {
                DefaultCodeAccount(state, Sender);
                return FrameTx([ExpiryFrame(9999), SelfVerifyFrame()], [Secp(Sender)]);
            }, FrameTxPayerOutcome.Resolved, Sender);

        yield return Case("ExpiryFrameOnly_NoPayer",
            state =>
            {
                DefaultCodeAccount(state, Sender);
                return FrameTx([ExpiryFrame(9999)], []);
            }, FrameTxPayerOutcome.NoPayer, null);
    }

    [Test]
    public void Resolve_SelfPaid_CapturesSenderAndPayerDependencies()
    {
        TestReadOnlyStateProvider state = new();
        state.CreateAccount(Sender, wei: Eth(7), nonce: 5);
        Transaction tx = FrameTx([SelfVerifyFrame()], [Secp(Sender)]);

        FrameTxPayerResolution resolution = Resolve(tx, state);
        FrameTxDependencySet deps = resolution.Dependencies;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resolution.Outcome, Is.EqualTo(FrameTxPayerOutcome.Resolved));
            Assert.That(deps.SenderCodeHash, Is.EqualTo(Keccak.OfAnEmptyString.ValueHash256));
            Assert.That(deps.SenderNonce, Is.EqualTo(5UL));
            Assert.That(deps.Payer, Is.EqualTo(Sender));
            Assert.That(deps.PayerBalance, Is.EqualTo(Eth(7)));
            Assert.That(deps.DependsOnExpiry, Is.False);
        }
    }

    [Test]
    public void Resolve_Sponsored_DoesNotNamePayerFromUnverifiedSignature()
    {
        // A forged pay-frame signature must not resolve to an arbitrary victim: frame signatures are
        // unverified at admission, so a third-party payer is never named natively.
        TestReadOnlyStateProvider state = new();
        state.CreateAccount(Sender, wei: 0, nonce: 1);
        state.CreateAccount(Sponsor, wei: Eth(3), nonce: 0);
        Transaction tx = FrameTx([OnlyVerifyFrame(), PayFrame(Sponsor)], [Secp(Sender), Secp(Sponsor)]);

        FrameTxPayerResolution resolution = Resolve(tx, state);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resolution.Outcome, Is.EqualTo(FrameTxPayerOutcome.RequiresSimulation));
            Assert.That(resolution.Payer, Is.Null);
            Assert.That(resolution.Dependencies.Payer, Is.Null);
        }
    }

    [Test]
    public void Resolve_ExpiryPrefix_CapturesDeadlineAndVerifierCode()
    {
        TestReadOnlyStateProvider state = new();
        DefaultCodeAccount(state, Sender);
        state.InsertCode(Eip8141Constants.ExpiryVerifierCode, Eip8141Constants.ExpiryVerifierAddress);
        Transaction tx = FrameTx([ExpiryFrame(0xDEAD_BEEF), SelfVerifyFrame()], [Secp(Sender)]);

        FrameTxDependencySet deps = Resolve(tx, state).Dependencies;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(deps.DependsOnExpiry, Is.True);
            Assert.That(deps.ExpiryDeadline, Is.EqualTo(0xDEAD_BEEFUL));
            Assert.That(deps.ExpiryVerifierCodeHash, Is.EqualTo(Keccak.Compute(Eip8141Constants.ExpiryVerifierCode).ValueHash256));
        }
    }

    [Test]
    public void Resolve_DeployPrefix_AgreesWithValidationPricing()
    {
        // Admission pricing and payer resolution must classify this prefix the same way, or they drift.
        TestReadOnlyStateProvider state = new();
        DefaultCodeAccount(state, Sender);
        TxFrame trailing = new(TxFrame.ModeDefault, flags: 0, target: null, gasLimit: 5_000_000, UInt256.Zero, default);
        Transaction tx = FrameTx([DeployFrame(), SelfVerifyFrame(), trailing], [Secp(Sender)]);

        FrameTxPayerResolution resolution = Resolve(tx, state);
        ulong verifyGas = FrameTxValidation.ValidationWorkGas(tx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resolution.Outcome, Is.EqualTo(FrameTxPayerOutcome.Resolved));
            Assert.That(resolution.Payer, Is.EqualTo(Sender));
            // Recognized-prefix pricing stops at the self_verify frame, excluding the trailing frame's gas.
            Assert.That(verifyGas, Is.EqualTo(50_000UL + 100_000UL + Eip8141Constants.Secp256k1VerificationGasCost));
        }
    }

    private static TestCaseData Case(string name, Func<TestReadOnlyStateProvider, Transaction> build, FrameTxPayerOutcome outcome, Address? payer) =>
        new TestCaseData(build, outcome, payer).SetName($"Resolve_{name}");

    private static UInt256 Eth(int amount) => (UInt256)amount * Unit.Ether;

    private static void DefaultCodeAccount(TestReadOnlyStateProvider state, Address address) =>
        state.CreateAccount(address, wei: Eth(1), nonce: 0);

    private static void DeployedCodeAccount(TestReadOnlyStateProvider state, Address address) =>
        state.InsertCode([0x60, 0x00], address);

    private static Transaction FrameTx(TxFrame[] frames, TxFrameSignature[] signatures) => new()
    {
        Type = TxType.FrameTx,
        SenderAddress = Sender,
        Frames = frames,
        FrameSignatures = signatures,
    };

    private static TxFrameSignature Secp(Address signer) =>
        new(TxFrameSignature.SchemeSecp256k1, signer, default, new byte[TxFrameSignature.Secp256k1SignatureLength]);

    private static TxFrame SelfVerifyFrame() =>
        new(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, gasLimit: 100_000, UInt256.Zero, default);

    private static TxFrame OnlyVerifyFrame() =>
        new(TxFrame.ModeVerify, TxFrame.ApproveExecution, target: null, gasLimit: 100_000, UInt256.Zero, default);

    private static TxFrame PayFrame(Address target) =>
        new(TxFrame.ModeVerify, TxFrame.ApprovePayment, target, gasLimit: 100_000, UInt256.Zero, default);

    private static TxFrame ExpiryFrame(ulong deadline)
    {
        byte[] data = new byte[Eip8141Constants.ExpiryDataLength];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(data, deadline);
        return new TxFrame(TxFrame.ModeVerify, flags: 0, Eip8141Constants.ExpiryVerifierAddress, gasLimit: 30_000, UInt256.Zero, data);
    }

    private static TxFrame Frame(byte mode) => new(mode, flags: 0, target: null, gasLimit: 50_000, UInt256.Zero, default);

    private static TxFrame DeployFrame() => Frame(TxFrame.ModeDefault);
}
