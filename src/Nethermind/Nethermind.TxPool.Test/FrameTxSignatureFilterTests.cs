// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.TxPool.Filters;
using NSubstitute;
using NUnit.Framework;
using static Nethermind.Core.Test.Builders.FrameTxTestFrames;

namespace Nethermind.TxPool.Test;

/// <summary>The EIP-8141 <c>validate_signature</c> gate as the pool applies it: a frame transaction carries an
/// explicit sender, so this is the only place a bad signature is caught before admission.</summary>
// The cases assert against the shared rejection counter.
[NonParallelizable]
internal class FrameTxSignatureFilterTests
{
    private static readonly IEthereumEcdsa EthereumEcdsa = new EthereumEcdsa(TestBlockchainIds.ChainId);

    private static IEnumerable<TestCaseData> SignatureCases()
    {
        static TestCaseData Case(string name, Func<Transaction> build, string? expectedError) =>
            new TestCaseData(build, expectedError).SetName($"Accept_{name}");

        yield return Case("AnAbsentSignatureListPassesVacuously",
            static () => FrameTx(TestItem.AddressA, [], SelfVerify(PrefixFrameGas)), null);

        yield return Case("ASignatureByTheSenderIsAdmissible",
            static () => Signed(TestItem.PrivateKeyA, signer: null), null);

        yield return Case("ASignatureNamingItsOwnSignerIsAdmissible",
            static () => Signed(TestItem.PrivateKeyB, signer: TestItem.PrivateKeyB.Address), null);

        yield return Case("ASignatureOverAnExplicitDigestIsAdmissible",
            static () => SignedOverDigest(TestItem.PrivateKeyB), null);

        yield return Case("ASignatureByAnotherKeyIsRejected",
            static () => Signed(TestItem.PrivateKeyB, signer: TestItem.PrivateKeyC.Address),
            FrameTxSignatureValidator.InvalidSecp256k1Signer);

        yield return Case("ASignatureOfTheWrongLengthIsRejected",
            static () => FrameTx(TestItem.AddressA,
                [new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, TestItem.AddressA, default, new byte[64])],
                SelfVerify(PrefixFrameGas)),
            FrameTxSignatureValidator.InvalidSignatureLength);

        yield return Case("ALegacyRecoveryIdIsRejected", static () =>
        {
            Transaction tx = Signed(TestItem.PrivateKeyA, signer: null);
            byte[] raw = tx.FrameSignatures![0].Signature.ToArray();
            raw[0] += 27;
            tx.FrameSignatures = [new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, null, default, raw)];
            return tx;
        }, FrameTxSignatureValidator.NonCanonicalSignature);

        // The signature hash covers the frame list, so the pool must refuse a payload edited after signing.
        yield return Case("AFrameEditedAfterSigningIsRejected", static () =>
        {
            Transaction tx = Signed(TestItem.PrivateKeyA, signer: null);
            tx.Frames = [SelfVerify(PrefixFrameGas), Execution()];
            return tx;
        }, FrameTxSignatureValidator.InvalidSecp256k1Signer);

        // An ARBITRARY witness is verified by frame code, so the pool only checks its shape.
        yield return Case("AnArbitraryWitnessIsLeftToFrameCode",
            static () => FrameTx(TestItem.AddressA,
                [new TxFrameSignature(TxFrameSignature.SchemeArbitrary, null, default, new byte[] { 0xde, 0xad })],
                SelfVerify(PrefixFrameGas)),
            null);
    }

    [TestCaseSource(nameof(SignatureCases))]
    public void Accept_AppliesValidateSignatureBeforeAdmission(Func<Transaction> build, string? expectedError)
    {
        Transaction tx = build();
        long before = Metrics.PendingTransactionsFrameTxSignatureInvalid;

        AcceptTxResult result = Accept(tx, out bool verified);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result == AcceptTxResult.Accepted, Is.EqualTo(expectedError is null), result.ToString());
            if (expectedError is not null)
            {
                Assert.That(result.ToString(), Does.Contain(expectedError));
            }

            Assert.That(verified, Is.EqualTo(expectedError is null));
            Assert.That(Metrics.PendingTransactionsFrameTxSignatureInvalid,
                Is.EqualTo(expectedError is null ? before : before + 1));
        }
    }

    [Test]
    public void Accept_LeavesANonFrameTransactionAlone()
    {
        Transaction tx = Build.A.Transaction.WithType(TxType.EIP1559).WithSenderAddress(TestItem.AddressA).TestObject;

        Assert.That(Accept(tx, out bool verified), Is.EqualTo(AcceptTxResult.Accepted));
        Assert.That(verified, Is.True);
    }

    private static AcceptTxResult Accept(Transaction tx, out bool signaturesVerified)
    {
        IChainHeadSpecProvider specProvider = Substitute.For<IChainHeadSpecProvider>();
        specProvider.GetCurrentHeadSpec().Returns(Eip8141Prototype.Instance);
        FrameTxSignatureFilter filter = new(specProvider, EthereumEcdsa, LimboLogs.Instance.GetClassLogger<FrameTxSignatureFilterTests>());
        TxFilteringState state = new(tx, Substitute.For<IAccountStateProvider>(), Eip8141Prototype.Instance);

        AcceptTxResult result = filter.Accept(tx, ref state, TxHandlingOptions.None);
        signaturesVerified = state.FrameSignaturesVerified;
        return result;
    }

    private static Transaction Signed(PrivateKey key, Address? signer)
    {
        Transaction tx = FrameTx(TestItem.AddressA, [], SelfVerify(PrefixFrameGas));
        SignSecp256k1(tx, key, signer);
        return tx;
    }

    private static Transaction SignedOverDigest(PrivateKey key)
    {
        byte[] digest = new byte[Hash256.Size];
        digest[31] = 1;
        Transaction tx = FrameTx(TestItem.AddressA, [], SelfVerify(PrefixFrameGas));
        // An explicit digest is signed as given, so the entry needs no canonical-hash round trip.
        byte[] raw = Secp256k1SignatureBytes(new Ecdsa().Sign(key, new ValueHash256(digest)));
        tx.FrameSignatures = [new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, key.Address, digest, raw)];
        return tx;
    }
}
