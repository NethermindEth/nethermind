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

/// <summary>
/// Native payer resolution over EIP-8141 legible validation prefixes: the default-code
/// <c>self_verify</c> and <c>only_verify | pay</c> shapes resolve, deployed code defers to
/// simulation, and a prefix that never approves payment resolves to no payer.
/// </summary>
public class FrameTxPayerResolverTests
{
    private static readonly Address Sender = TestItem.AddressA;
    private static readonly Address Sponsor = TestItem.AddressB;

    [TestCaseSource(nameof(OutcomeCases))]
    public void Resolve_OutcomeMatrix(Func<TestReadOnlyStateProvider, Transaction> build, FrameTxPayerOutcome expectedOutcome, Address? expectedPayer)
    {
        TestReadOnlyStateProvider state = new();
        Transaction tx = build(state);

        FrameTxPayerResolution resolution = FrameTxPayerResolver.Resolve(tx, state);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resolution.Outcome, Is.EqualTo(expectedOutcome));
            Assert.That(resolution.Payer, Is.EqualTo(expectedPayer));
        }
    }

    private static IEnumerable<TestCaseData> OutcomeCases()
    {
        // Self relay: a default-code EOA pays for itself.
        yield return Case("SelfVerify_DefaultCodeSender_PayerIsSender",
            state =>
            {
                DefaultCodeAccount(state, Sender);
                return FrameTx([SelfVerifyFrame()], [Secp(Sender)]);
            }, FrameTxPayerOutcome.Resolved, Sender);

        // Canonical-paymaster relay with a default-code EOA sponsor: the sponsor pays.
        yield return Case("OnlyVerifyPay_DefaultCodeSponsor_PayerIsSponsor",
            state =>
            {
                DefaultCodeAccount(state, Sender);
                DefaultCodeAccount(state, Sponsor);
                return FrameTx([OnlyVerifyFrame(), PayFrame(Sponsor)], [Secp(Sender), Secp(Sponsor)]);
            }, FrameTxPayerOutcome.Resolved, Sponsor);

        // A never-before-seen default-code sender still resolves (empty code hash).
        yield return Case("SelfVerify_NonExistentSender_PayerIsSender",
            _ => FrameTx([SelfVerifyFrame()], [Secp(Sender)]),
            FrameTxPayerOutcome.Resolved, Sender);

        // only_verify approves the sender but no pay frame follows: payment is never approved.
        yield return Case("OnlyVerifyWithoutPay_NoPayer",
            state =>
            {
                DefaultCodeAccount(state, Sender);
                return FrameTx([OnlyVerifyFrame()], [Secp(Sender)]);
            }, FrameTxPayerOutcome.NoPayer, null);

        // A leading DEFAULT frame is not a recognized legible prefix.
        yield return Case("DefaultFrameFirst_RequiresSimulation",
            state =>
            {
                DefaultCodeAccount(state, Sender);
                return FrameTx([Frame(TxFrame.ModeDefault), SelfVerifyFrame()], [Secp(Sender)]);
            }, FrameTxPayerOutcome.RequiresSimulation, null);

        // A deployed smart-account sender must be simulated.
        yield return Case("SelfVerify_DeployedCodeSender_RequiresSimulation",
            state =>
            {
                DeployedCodeAccount(state, Sender);
                return FrameTx([SelfVerifyFrame()], [Secp(Sender)]);
            }, FrameTxPayerOutcome.RequiresSimulation, null);

        // A code-carrying pay target is a paymaster (canonical hash unpinned / non-canonical).
        yield return Case("OnlyVerifyPay_DeployedCodePaymaster_RequiresSimulation",
            state =>
            {
                DefaultCodeAccount(state, Sender);
                DeployedCodeAccount(state, Sponsor);
                return FrameTx([OnlyVerifyFrame(), PayFrame(Sponsor)], [Secp(Sender), Secp(Sponsor)]);
            }, FrameTxPayerOutcome.RequiresSimulation, null);

        // Structural signature-shape failure in a legible frame: execution would revert it.
        yield return Case("SelfVerify_WrongSignatureScheme_NoPayer",
            state =>
            {
                DefaultCodeAccount(state, Sender);
                return FrameTx([SelfVerifyFrame()], [new TxFrameSignature(TxFrameSignature.SchemeP256, Sender, default, new byte[128])]);
            }, FrameTxPayerOutcome.NoPayer, null);

        // The sponsor signature at index 1 must name the pay target, not resolve to the sender.
        yield return Case("OnlyVerifyPay_SponsorSignatureMissing_NoPayer",
            state =>
            {
                DefaultCodeAccount(state, Sender);
                DefaultCodeAccount(state, Sponsor);
                return FrameTx([OnlyVerifyFrame(), PayFrame(Sponsor)], [Secp(Sender), Secp(Sender)]);
            }, FrameTxPayerOutcome.NoPayer, null);

        // A leading expiry_verify frame is skipped for shape matching; the self relay still resolves.
        yield return Case("ExpiryThenSelfVerify_PayerIsSender",
            state =>
            {
                DefaultCodeAccount(state, Sender);
                return FrameTx([ExpiryFrame(9999), SelfVerifyFrame()], [Secp(Sender)]);
            }, FrameTxPayerOutcome.Resolved, Sender);

        // A prefix consisting only of an expiry frame never sets a payer.
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

        FrameTxPayerResolution resolution = FrameTxPayerResolver.Resolve(tx, state);
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
    public void Resolve_Sponsored_CapturesSponsorBalanceAsPayerDependency()
    {
        TestReadOnlyStateProvider state = new();
        state.CreateAccount(Sender, wei: 0, nonce: 1);
        state.CreateAccount(Sponsor, wei: Eth(3), nonce: 0);
        Transaction tx = FrameTx([OnlyVerifyFrame(), PayFrame(Sponsor)], [Secp(Sender), Secp(Sponsor)]);

        FrameTxPayerResolution resolution = FrameTxPayerResolver.Resolve(tx, state);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resolution.Payer, Is.EqualTo(Sponsor));
            Assert.That(resolution.Dependencies.Payer, Is.EqualTo(Sponsor));
            Assert.That(resolution.Dependencies.PayerBalance, Is.EqualTo(Eth(3)));
        }
    }

    [Test]
    public void Resolve_ExpiryPrefix_CapturesDeadlineAndVerifierCode()
    {
        TestReadOnlyStateProvider state = new();
        DefaultCodeAccount(state, Sender);
        state.InsertCode(Eip8141Constants.ExpiryVerifierCode, Eip8141Constants.ExpiryVerifierAddress);
        Transaction tx = FrameTx([ExpiryFrame(0xDEAD_BEEF), SelfVerifyFrame()], [Secp(Sender)]);

        FrameTxDependencySet deps = FrameTxPayerResolver.Resolve(tx, state).Dependencies;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(deps.DependsOnExpiry, Is.True);
            Assert.That(deps.ExpiryDeadline, Is.EqualTo(0xDEAD_BEEFUL));
            Assert.That(deps.ExpiryVerifierCodeHash, Is.EqualTo(Keccak.Compute(Eip8141Constants.ExpiryVerifierCode).ValueHash256));
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
}
