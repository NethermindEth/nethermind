// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;
using static Nethermind.Core.Test.Builders.FrameTxTestFrames;

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
        return FrameTxPayerResolver.Resolve(tx, senderAccount);
    }

    private static IEnumerable<TestCaseData> OutcomeCases()
    {
        yield return Case("SelfVerify_DefaultCodeSender_PayerIsSender",
            state =>
            {
                DefaultCodeAccount(state, Sender);
                return FrameTx([SelfVerify(PrefixFrameGas)], [Secp256k1Signature(Sender)]);
            }, FrameTxPayerOutcome.Resolved, Sender);

        // The sponsor's pay-frame signature is unverified at admission, so a third party is never named natively.
        yield return Case("OnlyVerifyPay_DefaultCodeSponsor_RequiresSimulation",
            state =>
            {
                DefaultCodeAccount(state, Sender);
                DefaultCodeAccount(state, Sponsor);
                return FrameTx([OnlyVerify(PrefixFrameGas), Pay(Sponsor, PrefixFrameGas)], [Secp256k1Signature(Sender), Secp256k1Signature(Sponsor)]);
            }, FrameTxPayerOutcome.RequiresSimulation, null);

        // A following pay frame can name a sponsor if the sender's balance drops below max cost, so the
        // sender is not resolved natively — defer to simulation.
        yield return Case("SelfVerifyThenPay_RequiresSimulation",
            state =>
            {
                DefaultCodeAccount(state, Sender);
                DefaultCodeAccount(state, Sponsor);
                return FrameTx([SelfVerify(PrefixFrameGas), Pay(Sponsor, PrefixFrameGas)], [Secp256k1Signature(Sender), Secp256k1Signature(Sponsor)]);
            }, FrameTxPayerOutcome.RequiresSimulation, null);

        // A never-seen sender still resolves: the zeroed account reads as default (empty) code.
        yield return Case("SelfVerify_NonExistentSender_PayerIsSender",
            _ => FrameTx([SelfVerify(PrefixFrameGas)], [Secp256k1Signature(Sender)]),
            FrameTxPayerOutcome.Resolved, Sender);

        yield return Case("OnlyVerifyWithoutPay_NoPayer",
            state =>
            {
                DefaultCodeAccount(state, Sender);
                return FrameTx([OnlyVerify(PrefixFrameGas)], [Secp256k1Signature(Sender)]);
            }, FrameTxPayerOutcome.NoPayer, null);

        // NoPayer is code-independent: even a deployed sender cannot approve payment on a prefix with no
        // following pay frame.
        yield return Case("OnlyVerifyWithoutPay_DeployedCodeSender_NoPayer",
            state =>
            {
                DeployedCodeAccount(state, Sender);
                return FrameTx([OnlyVerify(PrefixFrameGas)], [Secp256k1Signature(Sender)]);
            }, FrameTxPayerOutcome.NoPayer, null);

        // A leading deploy frame is skipped like the expiry frame, but it also falsifies the default-code
        // inference behind it: by the time the VERIFY frame runs, the deploy frame has installed code at
        // tx.sender, so the frame dispatches that contract rather than the default code.
        yield return Case("DeployThenSelfVerify_RequiresSimulation",
            state =>
            {
                DefaultCodeAccount(state, Sender);
                return FrameTx([DeployFrame(), SelfVerify(PrefixFrameGas)], [Secp256k1Signature(Sender)]);
            }, FrameTxPayerOutcome.RequiresSimulation, null);

        // At most one deploy frame is skipped, so the second one is the frame that must name the payer and
        // does not: the same layout RecognizedPrefixLength rejects, keeping resolution and pricing aligned.
        yield return Case("TwoDeploysThenSelfVerify_RequiresSimulation",
            state =>
            {
                DefaultCodeAccount(state, Sender);
                return FrameTx([DeployFrame(), DeployFrame(), SelfVerify(PrefixFrameGas)], [Secp256k1Signature(Sender)]);
            }, FrameTxPayerOutcome.RequiresSimulation, null);

        yield return Case("DeployFrameOnly_NoPayer",
            state =>
            {
                DefaultCodeAccount(state, Sender);
                return FrameTx([DeployFrame()], [Secp256k1Signature(Sender)]);
            }, FrameTxPayerOutcome.NoPayer, null);

        yield return Case("SelfVerify_DeployedCodeSender_RequiresSimulation",
            state =>
            {
                DeployedCodeAccount(state, Sender);
                return FrameTx([SelfVerify(PrefixFrameGas)], [Secp256k1Signature(Sender)]);
            }, FrameTxPayerOutcome.RequiresSimulation, null);

        yield return Case("OnlyVerifyPay_DeployedCodePaymaster_RequiresSimulation",
            state =>
            {
                DefaultCodeAccount(state, Sender);
                DeployedCodeAccount(state, Sponsor);
                return FrameTx([OnlyVerify(PrefixFrameGas), Pay(Sponsor, PrefixFrameGas)], [Secp256k1Signature(Sender), Secp256k1Signature(Sponsor)]);
            }, FrameTxPayerOutcome.RequiresSimulation, null);

        // A wrong signature shape isn't proof of invalidity (its placement is unsettled), so it's deferred, not dropped.
        yield return Case("SelfVerify_WrongSignatureScheme_RequiresSimulation",
            state =>
            {
                DefaultCodeAccount(state, Sender);
                return FrameTx([SelfVerify(PrefixFrameGas)], [new TxFrameSignature(TxFrameSignature.SchemeP256, Sender, default, new byte[128])]);
            }, FrameTxPayerOutcome.RequiresSimulation, null);

        // An empty top-level signature list is deferred, not dropped: where the signature belongs is unsettled.
        yield return Case("SelfVerify_EmptySignatureList_RequiresSimulation",
            state =>
            {
                DefaultCodeAccount(state, Sender);
                return FrameTx([SelfVerify(PrefixFrameGas)], []);
            }, FrameTxPayerOutcome.RequiresSimulation, null);

        yield return Case("OnlyVerifyPay_SponsorSignatureShape_RequiresSimulation",
            state =>
            {
                DefaultCodeAccount(state, Sender);
                DefaultCodeAccount(state, Sponsor);
                return FrameTx([OnlyVerify(PrefixFrameGas), Pay(Sponsor, PrefixFrameGas)], [Secp256k1Signature(Sender), Secp256k1Signature(Sender)]);
            }, FrameTxPayerOutcome.RequiresSimulation, null);

        // A leading expiry_verify frame is skipped for shape matching; the self relay still resolves.
        yield return Case("ExpiryThenSelfVerify_PayerIsSender",
            state =>
            {
                DefaultCodeAccount(state, Sender);
                return FrameTx([ExpiryAt(9999), SelfVerify(PrefixFrameGas)], [Secp256k1Signature(Sender)]);
            }, FrameTxPayerOutcome.Resolved, Sender);

        // The only shape where both prefix skips apply; the deploy frame still forces simulation.
        yield return Case("ExpiryThenDeployThenSelfVerify_RequiresSimulation",
            state =>
            {
                DefaultCodeAccount(state, Sender);
                return FrameTx([ExpiryAt(9999), DeployFrame(), SelfVerify(PrefixFrameGas)], [Secp256k1Signature(Sender)]);
            }, FrameTxPayerOutcome.RequiresSimulation, null);

        yield return Case("ExpiryFrameOnly_NoPayer",
            state =>
            {
                DefaultCodeAccount(state, Sender);
                return FrameTx([ExpiryAt(9999)], []);
            }, FrameTxPayerOutcome.NoPayer, null);
    }

    [Test]
    public void Resolve_Sponsored_DoesNotNamePayerFromUnverifiedSignature()
    {
        // A forged pay-frame signature must not resolve to an arbitrary victim: frame signatures are
        // unverified at admission, so a third-party payer is never named natively.
        TestReadOnlyStateProvider state = new();
        state.CreateAccount(Sender, wei: 0, nonce: 1);
        state.CreateAccount(Sponsor, wei: Eth(3), nonce: 0);
        Transaction tx = FrameTx([OnlyVerify(PrefixFrameGas), Pay(Sponsor, PrefixFrameGas)], [Secp256k1Signature(Sender), Secp256k1Signature(Sponsor)]);

        FrameTxPayerResolution resolution = Resolve(tx, state);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resolution.Outcome, Is.EqualTo(FrameTxPayerOutcome.RequiresSimulation));
            Assert.That(resolution.Payer, Is.Null);
        }
    }

    [Test]
    public void Resolve_DeployPrefix_AgreesWithValidationPricing()
    {
        // Admission pricing and payer resolution must recognise the same prefix, or they drift: both take the
        // deploy frame as part of it, pricing by charging for it and resolution by deferring to simulation.
        TestReadOnlyStateProvider state = new();
        DefaultCodeAccount(state, Sender);
        TxFrame deploy = DeployFrame();
        TxFrame selfVerify = SelfVerify(PrefixFrameGas);
        TxFrame trailing = new(TxFrame.ModeDefault, flags: 0, target: null, gasLimit: 5_000_000, UInt256.Zero, default);
        Transaction tx = FrameTx([deploy, selfVerify, trailing], [Secp256k1Signature(Sender)]);

        FrameTxPayerResolution resolution = Resolve(tx, state);
        ulong verifyGas = FrameTxValidation.ValidationWorkGas(tx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resolution.Outcome, Is.EqualTo(FrameTxPayerOutcome.RequiresSimulation));
            Assert.That(resolution.Payer, Is.Null);
            // Recognized-prefix pricing stops at the self_verify frame, excluding the trailing frame's gas.
            Assert.That(verifyGas, Is.EqualTo(deploy.GasLimit + selfVerify.GasLimit + Eip8141Constants.Secp256k1VerificationGasCost));
        }
    }

    private static TestCaseData Case(string name, Func<TestReadOnlyStateProvider, Transaction> build, FrameTxPayerOutcome outcome, Address? payer) =>
        new TestCaseData(build, outcome, payer).SetName($"Resolve_{name}");

    private static UInt256 Eth(int amount) => (UInt256)amount * Unit.Ether;

    private static void DefaultCodeAccount(TestReadOnlyStateProvider state, Address address) =>
        state.CreateAccount(address, wei: Eth(1), nonce: 0);

    private static void DeployedCodeAccount(TestReadOnlyStateProvider state, Address address) =>
        state.InsertCode([0x60, 0x00], address);

    private static Transaction FrameTx(TxFrame[] frames, TxFrameSignature[] signatures) =>
        FrameTxTestFrames.FrameTx(Sender, signatures, frames);

    private static TxFrame Frame(byte mode) => new(mode, flags: 0, target: null, gasLimit: 50_000, UInt256.Zero, default);

    private static TxFrame DeployFrame() => Frame(TxFrame.ModeDefault);
}
