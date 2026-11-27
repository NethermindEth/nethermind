// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using Nethermind.Core;
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

/// <summary>The EIP-8141 claim gate as the pool applies it: a frame transaction carries an explicit sender, so this
/// is the only place a bad witness is caught before admission.</summary>
// The cases assert against the shared rejection counter.
[NonParallelizable]
internal class FrameTxClaimFilterTests
{
    private static readonly IEthereumEcdsa EthereumEcdsa = new EthereumEcdsa(TestBlockchainIds.ChainId);

    private static IEnumerable<TestCaseData> ClaimCases()
    {
        static TestCaseData Case(string name, Func<Transaction> build, string? expectedError, bool verified = true) =>
            new TestCaseData(build, expectedError, verified).SetName($"Accept_{name}");

        yield return Case("AnAbsentClaimListPassesVacuously",
            static () => FrameTx(TestItem.AddressA, [], SelfVerify(PrefixFrameGas)), null);

        yield return Case("AClaimByTheSenderIsAdmissible",
            static () => Signed(TestItem.PrivateKeyA, signer: null), null);

        yield return Case("AClaimNamingItsOwnKeyIsAdmissible",
            static () => Signed(TestItem.PrivateKeyB, signer: TestItem.PrivateKeyB.Address), null);

        yield return Case("AClaimOverAnExplicitDigestIsAdmissible",
            static () => SignedOverDigest(TestItem.PrivateKeyB), null);

        yield return Case("AWitnessByAnotherKeyIsRejected",
            static () => Signed(TestItem.PrivateKeyB, signer: TestItem.PrivateKeyC.Address),
            FrameTxClaimVerifier.InvalidClaim);

        yield return Case("AWitnessOfTheWrongLengthIsRejected", static () =>
        {
            Transaction tx = FrameTx(TestItem.AddressA, [Secp256k1Claim(TestItem.AddressA)], SelfVerify(PrefixFrameGas));
            tx.ClaimWitnesses = [new byte[64]];
            return tx;
        }, FrameTxClaimVerifier.InvalidWitnessLength);

        yield return Case("AClaimWithoutAWitnessIsRejected",
            static () => FrameTx(TestItem.AddressA, [Secp256k1Claim(TestItem.AddressA)], SelfVerify(PrefixFrameGas)),
            FrameTxClaimVerifier.MissingWitnesses);

        yield return Case("ALegacyRecoveryIdIsRejected", static () =>
        {
            Transaction tx = Signed(TestItem.PrivateKeyA, signer: null);
            tx.ClaimWitnesses![0][0] += 27;
            return tx;
        }, FrameTxClaimVerifier.InvalidClaim);

        // The signature hash covers the frame list, so the pool must refuse a payload edited after signing.
        yield return Case("AFrameEditedAfterSigningIsRejected", static () =>
        {
            Transaction tx = Signed(TestItem.PrivateKeyA, signer: null);
            tx.Frames = [SelfVerify(PrefixFrameGas), Execution()];
            return tx;
        }, FrameTxClaimVerifier.InvalidClaim);

        // A custom verifier runs in the pure context against state, so the filter leaves it to the simulation.
        yield return Case("ACustomVerifierClaimIsLeftToTheSimulation", static () =>
        {
            Transaction tx = FrameTx(TestItem.AddressA, [new TxFrameClaim(TestItem.AddressD, default, default, 1_000, 2)], SelfVerify(PrefixFrameGas));
            tx.ClaimWitnesses = [[0xde, 0xad]];
            return tx;
        }, null, verified: false);

        // The pool holds no leanSPHINCS verifier of its own, so the simulation verifies the claim.
        yield return Case("ALeanSphincsClaimIsLeftToTheSimulation", static () =>
        {
            Transaction tx = FrameTx(TestItem.AddressA, [new TxFrameClaim(Eip8141Constants.LeanSphincsVerifierAddress, TestItem.KeccakA.BytesToArray(), default, 0, 0)],
                SelfVerify(PrefixFrameGas));
            tx.ClaimWitnesses = [new byte[Eip8141Constants.LeanSphincsSignatureSize]];
            return tx;
        }, null, verified: false);
    }

    [TestCaseSource(nameof(ClaimCases))]
    public void Accept_VerifiesTheClaimsBeforeAdmission(Func<Transaction> build, string? expectedError, bool expectedVerified)
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

            Assert.That(verified, Is.EqualTo(expectedError is null && expectedVerified));
            Assert.That(Metrics.PendingTransactionsFrameTxSignatureInvalid,
                Is.EqualTo(expectedError is null ? before : before + 1));
        }
    }

    [Test]
    public void Accept_LeavesANonFrameTransactionAlone()
    {
        Transaction tx = Build.A.Transaction.WithType(TxType.EIP1559).WithSenderAddress(TestItem.AddressA).TestObject;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Accept(tx, out bool verified), Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(verified, Is.True);
        }
    }

    [TestCase(TxHandlingOptions.None, false, true, false)]
    [TestCase(TxHandlingOptions.None, false, true, true)]
    [TestCase(TxHandlingOptions.None, true, false, false)]
    [TestCase(TxHandlingOptions.PersistentBroadcast, false, false, false)]
    public void Accept_DuringBlockWork_DefersOnlyAGossipedVerification(TxHandlingOptions options, bool carriesBlobs, bool refetchable, bool building)
    {
        // Two claims: the first verification always runs, so only a later one can yield.
        Transaction tx = FrameTx(TestItem.AddressA, [], SelfVerify(PrefixFrameGas));
        if (carriesBlobs) tx.BlobVersionedHashes = [TestItem.KeccakA.BytesToArray()];
        SetDigestClaims(tx, (TestItem.PrivateKeyB, 1), (TestItem.PrivateKeyC, 2));
        IChainHeadInfoProvider headInfo = Substitute.For<IChainHeadInfoProvider>();
        if (building) headInfo.IsBuildingBlock.Returns(true);
        else headInfo.IsProcessingBlock.Returns(true);
        bool deferred = options == TxHandlingOptions.None;
        long invalid = Metrics.PendingTransactionsFrameTxSignatureInvalid;
        long preempted = Metrics.FrameTxSignatureVerificationsPreempted;

        FrameTxClaimFilter filter = CreateFilter(headInfo);
        TxFilteringState state = new(tx, Substitute.For<IAccountStateProvider>(), Eip8141Prototype.Instance);

        AcceptTxResult result = filter.Accept(tx, ref state, options);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(deferred ? AcceptTxResult.FrameSimulationDeferred : AcceptTxResult.Accepted), result.ToString());
            Assert.That(state.FrameClaimsVerified, Is.EqualTo(!deferred));
            Assert.That(state.FrameValidationYielded, Is.EqualTo(refetchable));
            Assert.That(state.FrameValidationRan, Is.EqualTo(!deferred), "a yield is not charged to the sending peer's validation budget");
            Assert.That(Metrics.PendingTransactionsFrameTxSignatureInvalid, Is.EqualTo(invalid), "a yield is not a verdict on the signature");
            Assert.That(Metrics.FrameTxSignatureVerificationsPreempted, Is.EqualTo(deferred ? preempted + 1 : preempted));
        }
    }

    [Test]
    public void Accept_DuringBlockWork_AdmitsASingleClaimTransaction()
    {
        IChainHeadInfoProvider headInfo = Substitute.For<IChainHeadInfoProvider>();
        headInfo.IsProcessingBlock.Returns(true);
        headInfo.IsBuildingBlock.Returns(true);
        FrameTxClaimFilter filter = CreateFilter(headInfo);
        Transaction tx = Signed(TestItem.PrivateKeyA, signer: null);
        TxFilteringState state = new(tx, Substitute.For<IAccountStateProvider>(), Eip8141Prototype.Instance);

        AcceptTxResult result = filter.Accept(tx, ref state, TxHandlingOptions.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(state.FrameClaimsVerified, Is.True);
        }
    }

    [Test]
    public void Accept_DuringBlockWork_RejectsAMalformedLaterEntry()
    {
        IChainHeadInfoProvider headInfo = Substitute.For<IChainHeadInfoProvider>();
        headInfo.IsProcessingBlock.Returns(true);
        FrameTxClaimFilter filter = CreateFilter(headInfo);
        Transaction tx = FrameTx(TestItem.AddressA, [], SelfVerify(PrefixFrameGas));
        SetDigestClaims(tx, (TestItem.PrivateKeyB, 1), (TestItem.PrivateKeyC, 2));
        tx.ClaimWitnesses![1] = new byte[64];
        TxFilteringState state = new(tx, Substitute.For<IAccountStateProvider>(), Eip8141Prototype.Instance);
        long invalid = Metrics.PendingTransactionsFrameTxSignatureInvalid;

        AcceptTxResult result = filter.Accept(tx, ref state, TxHandlingOptions.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ToString(), Does.Contain(FrameTxClaimVerifier.InvalidWitnessLength));
            Assert.That(state.FrameValidationYielded, Is.False);
            Assert.That(Metrics.PendingTransactionsFrameTxSignatureInvalid, Is.EqualTo(invalid + 1));
        }
    }

    private static AcceptTxResult Accept(Transaction tx, out bool claimsVerified)
    {
        FrameTxClaimFilter filter = CreateFilter();
        TxFilteringState state = new(tx, Substitute.For<IAccountStateProvider>(), Eip8141Prototype.Instance);

        AcceptTxResult result = filter.Accept(tx, ref state, TxHandlingOptions.None);
        claimsVerified = state.FrameClaimsVerified;
        return result;
    }

    private static FrameTxClaimFilter CreateFilter(IChainHeadInfoProvider? headInfo = null)
    {
        IChainHeadSpecProvider specProvider = Substitute.For<IChainHeadSpecProvider>();
        specProvider.GetCurrentHeadSpec().Returns(Eip8141Prototype.Instance);
        return new FrameTxClaimFilter(specProvider, EthereumEcdsa, LimboLogs.Instance.GetClassLogger<FrameTxClaimFilterTests>(), headInfo);
    }

    private static Transaction Signed(PrivateKey key, Address? signer)
    {
        Transaction tx = FrameTx(TestItem.AddressA, [], SelfVerify(PrefixFrameGas));
        SignSecp256k1(tx, key, signer);
        return tx;
    }

    private static Transaction SignedOverDigest(PrivateKey key)
    {
        Transaction tx = FrameTx(TestItem.AddressA, [], SelfVerify(PrefixFrameGas));
        SetDigestClaims(tx, (key, 1));
        return tx;
    }

    private static void SetDigestClaims(Transaction tx, params (PrivateKey Key, byte Fill)[] signers)
    {
        tx.FrameClaims = new TxFrameClaim[signers.Length];
        tx.ClaimWitnesses = new byte[signers.Length][];
        for (int i = 0; i < signers.Length; i++)
        {
            (tx.FrameClaims[i], tx.ClaimWitnesses[i]) = DigestClaim(signers[i].Key, signers[i].Fill);
        }
    }
}
