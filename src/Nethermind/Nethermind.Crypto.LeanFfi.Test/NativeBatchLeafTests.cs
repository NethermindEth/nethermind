// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Crypto.LeanFfi.Test;

[NonParallelizable]
public class NativeBatchLeafTests
{
    private static readonly ILeanProofVerifier Native = NativeLeanProofVerifier.Instance;

    private static readonly Eip8288Verifier Batched = new(Eip8288Verifier.BatchedAggregatedVk);

    // EIP-8288 Test Cases 2 and 12 under the batch-pricing guest: two transactions each declare one leanSTARK claim under one
    // key. A batch leaf or their singleton proofs, recursively aggregated, give a mode-1 wrapper over the same dependencies
    // and signature hashes; EIP-8288's guest takes no multi-claim leaf.
    [Test]
    public void Batch_leaf_and_singletons_build_the_same_mode_1_wrapper()
    {
        (ValueHash256[] claims, ValueHash256 key, ReadOnlyMemory<byte> leafProof) = FixtureLeaf();
        Transaction[] transactions = [.. claims.Select((claim, nonce) => DependentTransaction(nonce, new FrameDependency(Eip8288Constants.LeanStarkScheme, claim, key)))];
        ValueHash256[] signatureHashes = [.. transactions.Select(static tx => FrameTxSigHash.ComputeValue(tx))];
        List<FrameDependency> deps = Dependencies(transactions);
        ValueHash256 depsHash = Eip8288Dependencies.ComputeDepsHash(deps);
        BatchLeafInput leaf = new(key, claims, deps, leafProof);
        ReadOnlyMemory<byte>[] singletons = [NativeLeanProofVerifierTests.Fixture("stark-batch-singleton-0"),
            NativeLeanProofVerifierTests.Fixture("stark-batch-singleton-1")];

        byte[] batched = Native.ProveRecursiveStark(depsHash, Eip8288Constants.BatchedAggregatedVk, new() { BatchLeaves = [leaf] });
        byte[] tree = RecursiveStarkAggregator.Prove(new() { Deps = deps, Witnesses = singletons }, Native, Batched, in depsHash);

        foreach (byte[] proof in new[] { batched, tree })
        {
            MempoolWrapper wrapper = new()
            {
                Transactions = transactions,
                Deps = deps,
                Mode = MempoolWrapper.ModeRecursive,
                RecursiveStark = new(proof, new Hash256(depsHash))
            };
            Assert.That(MempoolWrapperValidator.Validate(wrapper, Native, Batched, out string error), Is.True, error);
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(transactions.Select(static tx => FrameTxSigHash.ComputeValue(tx)), Is.EqualTo(signatureHashes));
            Assert.That(Native.VerifyRecursiveStark(depsHash, Eip8288Constants.AggregatedVk, batched), Is.False);
            Assert.That(() => Native.ProveRecursiveStark(depsHash, Eip8288Constants.AggregatedVk, new() { BatchLeaves = [leaf] }),
                Throws.ArgumentException);
            // Mode 0 stays singleton: the leaf is no direct proof of either claim, nor is one singleton enough for both. EIP-8288's
            // guest takes a direct proof of the bare claim, not of get_claim_hash(claim).
            Assert.That(Native.VerifyLeanStark(claims[0], key, leaf.Proof.Span, Eip8288Constants.BatchedAggregatedVk), Is.False);
            Assert.That(Native.VerifyLeanStark(claims[0], key, singletons[0].Span, Eip8288Constants.BatchedAggregatedVk), Is.True);
            Assert.That(Native.VerifyLeanStark(claims[0], key, singletons[0].Span, Eip8288Constants.AggregatedVk), Is.False);
            Assert.That(Native.VerifyLeanStark(claims[0], key, singletons[0].Span), Is.False);
            Assert.That(() => Native.ProveRecursiveStark(depsHash, Eip8288Constants.BatchedAggregatedVk,
                new() { Deps = deps[..1], Witnesses = singletons[..1] }), Throws.InvalidOperationException);
            Assert.That(() => Native.ProveRecursiveStark(depsHash, Eip8288Constants.BatchedAggregatedVk,
                new() { BatchLeaves = [leaf with { VerificationKeyHash = ValueKeccak.Compute("other key") }] }), Throws.InvalidOperationException);
            Assert.That(() => Native.ProveRecursiveStark(depsHash, Eip8288Constants.BatchedAggregatedVk,
                new() { BatchLeaves = [leaf with { Claims = [claims[1], claims[0]] }] }), Throws.InvalidOperationException);
        }
    }

    // ethereum/EIPs#12664 Test Cases: members declare batch claims and a sponsor the leaf payment; the block proof covers both
    // members or one with the payment, or the payment alone, never a claim without it, and only under the batch-pricing guest.
    [Test]
    public void Sponsored_leaf_proves_members_with_their_payment()
    {
        (ValueHash256[] claims, ValueHash256 key, ReadOnlyMemory<byte> leafProof) = FixtureLeaf();
        FrameDependency payment = new(Eip8288Constants.LeanStarkLeafPaymentScheme, BatchHash(claims), key);
        Transaction[] members = [.. claims.Select((claim, nonce) => DependentTransaction(nonce, new FrameDependency(Eip8288Constants.BatchedLeanStarkScheme, claim, key)))];
        Transaction sponsor = DependentTransaction(claims.Length, payment);

        void Proves(IReadOnlyList<Transaction> block, bool valid)
        {
            List<FrameDependency> deps = Dependencies(block);
            ValueHash256 depsHash = Eip8288Dependencies.ProofStatement(deps, Eip8288Dependencies.ComputeDepsHash(deps));
            BatchLeafInput leaf = new(key, claims, deps, leafProof);
            if (!valid)
            {
                Assert.That(() => Native.ProveRecursiveStark(depsHash, Eip8288Constants.BatchedAggregatedVk, new() { BatchLeaves = [leaf] }),
                    Throws.InvalidOperationException);
                return;
            }
            byte[] proof = Native.ProveRecursiveStark(depsHash, Eip8288Constants.BatchedAggregatedVk, new() { BatchLeaves = [leaf] });
            using (Assert.EnterMultipleScope())
            {
                Assert.That(Native.VerifyRecursiveStark(depsHash, Eip8288Constants.BatchedAggregatedVk, proof), Is.True);
                Assert.That(Native.VerifyRecursiveStark(Eip8288Dependencies.ComputeDepsHash(deps), Eip8288Constants.BatchedAggregatedVk, proof),
                    Is.False, "a finalized proof is no plain one");
                Assert.That(() => Native.ProveRecursiveStark(depsHash, Eip8288Constants.AggregatedVk, new() { BatchLeaves = [leaf] }),
                    Throws.ArgumentException);
            }
        }

        Proves([members[0], members[1], sponsor], true);
        Proves([members[1], sponsor], true);
        // A payment after every member expired is still proved, and charged; two sponsors of one leaf are one dependency.
        Proves([sponsor], true);
        Proves([members[0], sponsor, DependentTransaction(claims.Length + 1, payment)], true);
        Proves([members[1]], false);
    }

    private static (ValueHash256[] Claims, ValueHash256 Key, ReadOnlyMemory<byte> Proof) FixtureLeaf()
    {
        byte[] claimBytes = NativeLeanProofVerifierTests.Fixture("stark-batch-claims");
        return ([new(claimBytes.AsSpan(0, 32)), new(claimBytes.AsSpan(32, 32))], new(NativeLeanProofVerifierTests.Fixture("stark-batched-key")),
            NativeLeanProofVerifierTests.Fixture("stark-batch-proof"));
    }

    private static List<FrameDependency> Dependencies(IEnumerable<Transaction> transactions) => Eip8288Dependencies.Canonicalize(
        transactions.SelectMany(static tx => Eip8288Dependencies.ForTransaction(tx, FrameTxSigHash.ComputeValue)));

    /// <summary><c>get_batch_hash(claims)</c>: <c>keccak256("EIP8288:batch:v1" || claims)</c>.</summary>
    private static ValueHash256 BatchHash(ValueHash256[] claims)
    {
        byte[] preimage = [.. "EIP8288:batch:v1"u8, .. claims.SelectMany(static claim => claim.Bytes.ToArray())];
        return ValueKeccak.Compute(preimage);
    }

    // EIP-8288 Test Case 12: MAX_INNER_PROOFS_PER_STEP counts direct leanSTARK proofs, batch leaves and recursive proofs together.
    [TestCase(0, true)]
    [TestCase(1, false)]
    public void Inner_proofs_per_step_count_direct_starks_leaves_and_children(int extra, bool accepted)
    {
        FrameDependency stark = NativeLeanProofVerifierTests.Dependency("stark");
        FrameDependency sphincs = NativeLeanProofVerifierTests.Dependency("sphincs");
        BatchLeafInput leaf = new(stark.VerificationKey, [stark.DataHash], [stark], NativeLeanProofVerifierTests.Witness("stark"));
        RecursiveProofInput child = new([stark], [1]);
        AggregationInput input = new()
        {
            Deps = [sphincs, stark],
            Witnesses = [NativeLeanProofVerifierTests.Witness("sphincs"), NativeLeanProofVerifierTests.Witness("stark")],
            BatchLeaves = [leaf],
            RecursiveProofs = [.. Enumerable.Repeat(child, Eip8288Constants.MaxInnerProofsPerStep - 2 + extra)]
        };
        if (accepted) Assert.That(NativeLeanProofVerifier.SerializeInput(input, batchedGuest: true), Is.Not.Empty);
        else Assert.Throws<ArgumentException>(() => NativeLeanProofVerifier.SerializeInput(input, batchedGuest: true));
    }

    private static Transaction DependentTransaction(int nonce, FrameDependency dependency)
    {
        TxFrame[] frames = [FrameTxTestFrames.SelfVerify(FrameTxTestFrames.PrefixFrameGas),
            new(FrameMode.DepVerify, FrameFlags.None, null, dependency.FrameCharge(batchPricing: true),
                UInt256.Zero, Eip8288Dependencies.Serialize([dependency]))];
        Transaction transaction = new()
        {
            Type = TxType.FrameTx,
            ChainId = TestBlockchainIds.ChainId,
            Nonce = (ulong)nonce,
            SenderAddress = TestItem.PrivateKeyB.Address,
            NonceKeys = [],
            Frames = frames,
            GasLimit = FrameTxValidation.TotalGasLimit(frames),
            GasPrice = 1,
            DecodedMaxFeePerGas = 1,
        };
        transaction.Hash = transaction.CalculateHash();
        return transaction;
    }
}
