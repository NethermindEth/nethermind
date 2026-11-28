// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.ProofAggregation;

/// <summary>The EIP-8518 proof wrapper encoding and its checks against its transactions' claims.</summary>
public class ProofWrapperTests
{
    [Test]
    public void Normalize_EmptiesOnlyTheAggregatableWitnesses_AndKeepsTheConsensusHash()
    {
        Transaction tx = FrameTxTestFrames.LeanSphincsTx(0, withSecp256k1Claim: true, (TestItem.KeccakA, TestItem.KeccakB));

        Transaction decoded = Decode(ProofWrapperCodec.Normalize(tx)!);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.Hash, Is.EqualTo(tx.Hash));
            Assert.That(decoded.ClaimWitnesses![0], Is.Empty);
            Assert.That(decoded.ClaimWitnesses![1], Is.EqualTo(tx.ClaimWitnesses![1]));
        }
    }

    [Test]
    public void Normalize_DifferentRawSignatures_EncodeTheSame()
    {
        Transaction first = FrameTxTestFrames.LeanSphincsTx(0, withSecp256k1Claim: false, (TestItem.KeccakA, TestItem.KeccakB));
        Transaction second = FrameTxTestFrames.LeanSphincsTx(0, withSecp256k1Claim: false, (TestItem.KeccakA, TestItem.KeccakB));
        Array.Fill(second.ClaimWitnesses![0], (byte)0x42);

        Assert.That(ProofWrapperCodec.Normalize(second), Is.EqualTo(ProofWrapperCodec.Normalize(first)));
    }

    [Test]
    public void Normalize_WithoutAnAggregatableClaim_IsNull() =>
        Assert.That(ProofWrapperCodec.Normalize(FrameTxTestFrames.LeanSphincsTx(0, withSecp256k1Claim: true)), Is.Null);

    [Test]
    public void TryDecode_RoundTripsTheCanonicalWrapper_AndDeduplicatesRepeatedClaims()
    {
        ProofWrapper wrapper = Wrapper(
            FrameTxTestFrames.LeanSphincsTx(0, withSecp256k1Claim: true, (TestItem.KeccakA, TestItem.KeccakB), (TestItem.KeccakA, TestItem.KeccakB)),
            FrameTxTestFrames.LeanSphincsTx(1, withSecp256k1Claim: false, (TestItem.KeccakC, TestItem.KeccakD)));

        ProofWrapperCodec.Verdict verdict = ProofWrapperCodec.TryDecode(wrapper.Encoded, null, wrapper.Length, out ProofWrapper? decoded, out string? error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(verdict, Is.EqualTo(ProofWrapperCodec.Verdict.Valid), error);
            Assert.That(decoded!.Hash, Is.EqualTo(wrapper.Hash));
            Assert.That(decoded.Deps, Has.Count.EqualTo(2), "a repeated claim is one dependency");
            Assert.That(decoded.DepsHash, Is.EqualTo(Eip8288Dependencies.ComputeDepsHash(decoded.Deps)));
            Assert.That(decoded.TxsHash, Is.EqualTo(ProofWrapperCodec.ComputeTxsHash(decoded.Manifest)));
            Assert.That(decoded.Transactions[0].ClaimWitnesses, Has.Length.EqualTo(3), "every claim keeps its witness position");
        }
    }

    [Test]
    public void TryDecode_CompactReferences_ReconstructTheCanonicalWrapper()
    {
        Transaction tx = FrameTxTestFrames.LeanSphincsTx(0, withSecp256k1Claim: true, (TestItem.KeccakA, TestItem.KeccakB));
        ProofWrapper wrapper = Wrapper(tx);
        // The requester's cache entry comes from its pooled copy, normalized.
        byte[] cached = ProofWrapperCodec.Normalize(tx)!;
        byte[] compact = ProofWrapperCodec.Encode(wrapper.EncodedTransactions, wrapper.Deps, wrapper.Proof, static _ => true);

        ProofWrapperCodec.Verdict verdict = ProofWrapperCodec.TryDecode(compact,
            hash => hash == ProofWrapperCodec.EncodingHash(cached) ? cached : null, wrapper.Length, out ProofWrapper? decoded, out _);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(compact, Has.Length.LessThan(wrapper.Length));
            Assert.That(verdict, Is.EqualTo(ProofWrapperCodec.Verdict.Valid));
            Assert.That(decoded!.Hash, Is.EqualTo(wrapper.Hash));
        }
    }

    [TestCase(true, ProofWrapperCodec.Verdict.CacheMiss)]
    [TestCase(false, ProofWrapperCodec.Verdict.Malformed)]
    public void TryDecode_UnresolvedOrForbiddenReference(bool referencesAllowed, ProofWrapperCodec.Verdict expected)
    {
        ProofWrapper wrapper = Wrapper(FrameTxTestFrames.LeanSphincsTx(0, withSecp256k1Claim: false, (TestItem.KeccakA, TestItem.KeccakB)));
        byte[] compact = ProofWrapperCodec.Encode(wrapper.EncodedTransactions, wrapper.Deps, wrapper.Proof, static _ => true);

        Assert.That(ProofWrapperCodec.TryDecode(compact, referencesAllowed ? static _ => null : null, wrapper.Length, out _, out _),
            Is.EqualTo(expected));
    }

    [Test]
    public void TryDecode_ReconstructionBeyondTheAnnouncedLength_IsMalformed()
    {
        ProofWrapper wrapper = Wrapper(FrameTxTestFrames.LeanSphincsTx(0, withSecp256k1Claim: false, (TestItem.KeccakA, TestItem.KeccakB)));

        Assert.That(ProofWrapperCodec.TryDecode(wrapper.Encoded, null, wrapper.Length - 1, out _, out _), Is.EqualTo(ProofWrapperCodec.Verdict.Malformed));
    }

    [Test]
    public void TryDecode_TransactionsOutOfHashOrder_AreMalformed()
    {
        ProofWrapper wrapper = Wrapper(
            FrameTxTestFrames.LeanSphincsTx(0, withSecp256k1Claim: false, (TestItem.KeccakA, TestItem.KeccakB)),
            FrameTxTestFrames.LeanSphincsTx(1, withSecp256k1Claim: false, (TestItem.KeccakC, TestItem.KeccakD)));
        byte[] swapped = ProofWrapperCodec.Encode([wrapper.EncodedTransactions[1], wrapper.EncodedTransactions[0]], wrapper.Deps, wrapper.Proof);

        Assert.That(ProofWrapperCodec.TryDecode(swapped, null, swapped.Length, out _, out _), Is.EqualTo(ProofWrapperCodec.Verdict.Malformed));
    }

    public enum Defect { RetainedCoveredWitness, MissingUncoveredWitness, WrongWitnessCount, DepsMismatch, NoAggregatableClaim }

    [TestCase(Defect.RetainedCoveredWitness, ProofWrapperCodec.CoveredWitnessRetained)]
    [TestCase(Defect.MissingUncoveredWitness, ProofWrapperCodec.UncoveredWitnessMissing)]
    [TestCase(Defect.WrongWitnessCount, FrameTxClaimVerifier.WitnessCountMismatch)]
    [TestCase(Defect.DepsMismatch, ProofWrapperCodec.DepsMismatch)]
    [TestCase(Defect.NoAggregatableClaim, ProofWrapperCodec.NotAggregatable)]
    public void TryDecode_DependencyRuleViolation_IsInvalidWithoutBlame(Defect defect, string expectedError)
    {
        Transaction tx = FrameTxTestFrames.LeanSphincsTx(0, withSecp256k1Claim: true, (TestItem.KeccakA, TestItem.KeccakB));
        byte[] normalized = ProofWrapperCodec.Normalize(tx)!;
        Transaction broken = Decode(normalized);
        List<AggregatedDependency> deps = Eip8288Dependencies.Canonicalize(ProofWrapperCodec.AggregatedDependencies(tx));
        switch (defect)
        {
            case Defect.RetainedCoveredWitness:
                broken.ClaimWitnesses![0] = tx.ClaimWitnesses![0];
                break;
            case Defect.MissingUncoveredWitness:
                broken.ClaimWitnesses![1] = [];
                break;
            case Defect.WrongWitnessCount:
                broken.ClaimWitnesses = [broken.ClaimWitnesses![0]];
                break;
            case Defect.DepsMismatch:
                deps = [new AggregatedDependency(TestItem.KeccakE.ValueHash256, TestItem.KeccakF.ValueHash256)];
                break;
            case Defect.NoAggregatableClaim:
                broken = FrameTxTestFrames.LeanSphincsTx(0, withSecp256k1Claim: true);
                break;
        }

        byte[] encoded = ProofWrapperCodec.Encode([TxDecoder.Instance.Encode(broken, ProofWrapperCodec.TransactionBehaviors)], deps, [1, 2, 3]);

        ProofWrapperCodec.Verdict verdict = ProofWrapperCodec.TryDecode(encoded, null, encoded.Length, out _, out string? error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(verdict, Is.EqualTo(ProofWrapperCodec.Verdict.Invalid));
            Assert.That(error, Is.EqualTo(expectedError));
        }
    }

    [Test]
    public void VerifyProof_ChecksTheProofAgainstTheExactDependencyUnion_AndMarkCoveredMarksItsWitnesses()
    {
        FakeLeanProofVerifier verifier = new();
        Transaction tx = FrameTxTestFrames.LeanSphincsTx(0, withSecp256k1Claim: true, (TestItem.KeccakA, TestItem.KeccakB));
        ProofWrapper wrapper = Wrapper(verifier, tx);
        ProofWrapper forged = Wrapper(tx);
        ProofWrapperCodec.TryDecode(wrapper.Encoded, null, wrapper.Length, out ProofWrapper? received, out _);

        ProofWrapperCodec.MarkCovered(received!);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ProofWrapperCodec.VerifyProof(wrapper, verifier), Is.True);
            Assert.That(ProofWrapperCodec.VerifyProof(forged, verifier), Is.False);
            Assert.That(received!.Transactions[0].ClaimWitnesses![0], Is.SameAs(FrameTxClaimVerifier.AggregatedWitness));
            Assert.That(FrameTxClaimVerifier.HasAggregatedWitness(received.Transactions[0]), Is.True);
            Assert.That(received.Transactions[0].ClaimWitnesses![1], Is.EqualTo(tx.ClaimWitnesses![1]));
        }
    }

    /// <summary>A wrapper of <paramref name="transactions"/>, proven by <paramref name="verifier"/> or by a stand-in proof.</summary>
    internal static ProofWrapper Wrapper(FakeLeanProofVerifier? verifier, params Transaction[] transactions)
    {
        Array.Sort(transactions, static (left, right) => left.Hash!.CompareTo(right.Hash!));
        List<AggregatedDependency> deps = [];
        byte[][] encodings = new byte[transactions.Length][];
        for (int i = 0; i < transactions.Length; i++)
        {
            deps.AddRange(ProofWrapperCodec.AggregatedDependencies(transactions[i]));
            encodings[i] = ProofWrapperCodec.Normalize(transactions[i])!;
        }

        List<AggregatedDependency> canonical = Eip8288Dependencies.Canonicalize(deps);
        byte[] proof = verifier?.ProveDependencyProof(Eip8288Dependencies.ComputeDepsHash(canonical), Eip8288Constants.AggregatedVk, new AggregationInput())
            ?? [9, 9, 9];
        return ProofWrapperCodec.Create(encodings, transactions, canonical, proof);
    }

    private static ProofWrapper Wrapper(params Transaction[] transactions) => Wrapper(null, transactions);

    private static Transaction Decode(byte[] encoded)
    {
        Transaction tx = TxDecoder.Instance.DecodeCompleteNotNull(encoded, ProofWrapperCodec.TransactionBehaviors);
        return tx;
    }
}
