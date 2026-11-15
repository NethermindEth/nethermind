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

    // EIP-8288 Test Cases 2 and 12 under the batch guest (ethereum/EIPs#12664): two transactions each declare one leanSTARK claim under one
    // key. A batch leaf or their singleton proofs, recursively aggregated, give a mode-1 wrapper over the same dependencies
    // and signature hashes; EIP-8288's guest takes no multi-claim leaf.
    [Test]
    public void Batch_leaf_and_singletons_build_the_same_mode_1_wrapper()
    {
        (ValueHash256[] claims, ValueHash256 key, ReadOnlyMemory<byte> leafProof) = FixtureLeaf();
        Transaction[] transactions = [.. claims.Select((claim, nonce) => DependentTransaction(nonce, new FrameDependency(Eip8288Constants.LeanStarkScheme, claim, key)))];
        ValueHash256[] signatureHashes = [.. transactions.Select(static tx => FrameTxSigHash.ComputeValue(tx))];
        List<FrameDependency> deps = Dependencies(transactions);
        ValueHash256 depsHash = Batched.DepsHash(deps);
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
                RecursiveStark = new(proof, new Hash256(Eip8288Dependencies.ComputeDepsHash(deps)))
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

    // ethereum/EIPs#12664 Test Cases: a leaf folds into a proof covering exactly the selected members, the same leaf and signed
    // transactions either way, and only under the batch guest's scheme-inclusive digest.
    [TestCase(1)]
    [TestCase(2)]
    public void Leaf_folds_into_a_proof_of_the_selected_members(int members)
    {
        (ValueHash256[] claims, ValueHash256 key, ReadOnlyMemory<byte> leafProof) = FixtureLeaf();
        Transaction[] transactions = [.. claims.Take(members).Select((claim, nonce) =>
            DependentTransaction(nonce, new FrameDependency(Eip8288Constants.LeanStarkScheme, claim, key)))];
        List<FrameDependency> deps = Dependencies(transactions);
        ValueHash256 depsHash = Batched.DepsHash(deps);
        BatchLeafInput leaf = new(key, claims, deps, leafProof);

        byte[] proof = Native.ProveRecursiveStark(depsHash, Eip8288Constants.BatchedAggregatedVk, new() { BatchLeaves = [leaf] });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Native.VerifyRecursiveStark(depsHash, Eip8288Constants.BatchedAggregatedVk, proof), Is.True);
            Assert.That(Native.VerifyRecursiveStark(Eip8288Dependencies.ComputeDepsHash(deps), Eip8288Constants.BatchedAggregatedVk, proof),
                Is.False, "EIP-8288's split digest is another statement");
        }
    }

    private static (ValueHash256[] Claims, ValueHash256 Key, ReadOnlyMemory<byte> Proof) FixtureLeaf()
    {
        byte[] claimBytes = NativeLeanProofVerifierTests.Fixture("stark-batch-claims");
        return ([new(claimBytes.AsSpan(0, 32)), new(claimBytes.AsSpan(32, 32))], new(NativeLeanProofVerifierTests.Fixture("stark-batched-key")),
            NativeLeanProofVerifierTests.Fixture("stark-batch-proof"));
    }

    private static List<FrameDependency> Dependencies(IEnumerable<Transaction> transactions) => Eip8288Dependencies.Canonicalize(
        transactions.SelectMany(static tx => Eip8288Dependencies.ForTransaction(tx, FrameTxSigHash.ComputeValue)));

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

    internal static Transaction DependentTransaction(int nonce, FrameDependency dependency)
    {
        TxFrame[] frames = [FrameTxTestFrames.SelfVerify(FrameTxTestFrames.PrefixFrameGas),
            new(FrameMode.DepVerify, FrameFlags.None, null, dependency.VerificationGas,
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
