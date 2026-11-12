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

    // EIP-8288 Test Cases 2 and 12: two transactions each declare one leanSTARK claim under one key. A batch leaf or their
    // singleton proofs, recursively aggregated, give a mode-1 wrapper over the same dependencies and signature hashes.
    [Test]
    public void Batch_leaf_and_singletons_build_the_same_mode_1_wrapper()
    {
        byte[] claimBytes = NativeLeanProofVerifierTests.Fixture("stark-batch-claims");
        ValueHash256[] claims = [new(claimBytes.AsSpan(0, 32)), new(claimBytes.AsSpan(32, 32))];
        ValueHash256 key = new(NativeLeanProofVerifierTests.Fixture("stark-key"));
        Transaction[] transactions = [.. claims.Select((claim, nonce) => DependentTransaction(claim, key, nonce))];
        ValueHash256[] signatureHashes = [.. transactions.Select(static tx => FrameTxSigHash.ComputeValue(tx))];
        List<FrameDependency> deps = Eip8288Dependencies.Canonicalize(
            transactions.SelectMany(static tx => Eip8288Dependencies.ForTransaction(tx, FrameTxSigHash.ComputeValue)));
        ValueHash256 depsHash = Eip8288Dependencies.ComputeDepsHash(deps);
        BatchLeafInput leaf = new(key, claims, NativeLeanProofVerifierTests.Fixture("stark-batch-proof"));
        ReadOnlyMemory<byte>[] singletons = [NativeLeanProofVerifierTests.Fixture("stark-batch-singleton-0"),
            NativeLeanProofVerifierTests.Fixture("stark-batch-singleton-1")];

        byte[] batched = Native.ProveRecursiveStark(depsHash, Eip8288Constants.AggregatedVk, new() { BatchLeaves = [leaf] });
        byte[] tree = RecursiveStarkAggregator.Prove(new() { Deps = deps, Witnesses = singletons }, Native, Eip8288Verifier.Pinned, in depsHash);

        foreach (byte[] proof in new[] { batched, tree })
        {
            MempoolWrapper wrapper = new()
            {
                Transactions = transactions,
                Deps = deps,
                Mode = MempoolWrapper.ModeRecursive,
                RecursiveStark = new(proof, new Hash256(depsHash))
            };
            Assert.That(MempoolWrapperValidator.Validate(wrapper, Native, Eip8288Verifier.Pinned, out string error), Is.True, error);
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(transactions.Select(static tx => FrameTxSigHash.ComputeValue(tx)), Is.EqualTo(signatureHashes));
            // Mode 0 stays singleton: the leaf is no direct proof of either claim, nor is one singleton enough for both.
            Assert.That(Native.VerifyLeanStark(claims[0], key, leaf.Proof.Span), Is.False);
            Assert.That(Native.VerifyLeanStark(claims[0], key, singletons[0].Span), Is.True);
            Assert.That(() => Native.ProveRecursiveStark(depsHash, Eip8288Constants.AggregatedVk,
                new() { Deps = deps[..1], Witnesses = singletons[..1] }), Throws.InvalidOperationException);
            Assert.That(() => Native.ProveRecursiveStark(depsHash, Eip8288Constants.AggregatedVk,
                new() { BatchLeaves = [leaf with { VerificationKeyHash = ValueKeccak.Compute("other key") }] }), Throws.InvalidOperationException);
            Assert.That(() => Native.ProveRecursiveStark(depsHash, Eip8288Constants.AggregatedVk,
                new() { BatchLeaves = [leaf with { Claims = [claims[1], claims[0]] }] }), Throws.InvalidOperationException);
        }
    }

    // EIP-8288 Test Case 12: MAX_INNER_PROOFS_PER_STEP counts direct leanSTARK proofs, batch leaves and recursive proofs together.
    [TestCase(0, true)]
    [TestCase(1, false)]
    public void Inner_proofs_per_step_count_direct_starks_leaves_and_children(int extra, bool accepted)
    {
        FrameDependency stark = NativeLeanProofVerifierTests.Dependency("stark");
        FrameDependency sphincs = NativeLeanProofVerifierTests.Dependency("sphincs");
        BatchLeafInput leaf = new(stark.VerificationKey, [stark.DataHash], NativeLeanProofVerifierTests.Witness("stark"));
        RecursiveProofInput child = new([stark], [1]);
        AggregationInput input = new()
        {
            Deps = [sphincs, stark],
            Witnesses = [NativeLeanProofVerifierTests.Witness("sphincs"), NativeLeanProofVerifierTests.Witness("stark")],
            BatchLeaves = [leaf],
            RecursiveProofs = [.. Enumerable.Repeat(child, Eip8288Constants.MaxInnerProofsPerStep - 2 + extra)]
        };
        if (accepted) Assert.That(NativeLeanProofVerifier.SerializeInput(input), Is.Not.Empty);
        else Assert.Throws<ArgumentException>(() => NativeLeanProofVerifier.SerializeInput(input));
    }

    private static Transaction DependentTransaction(ValueHash256 claim, ValueHash256 key, int nonce)
    {
        FrameDependency dependency = new(Eip8288Constants.LeanStarkScheme, claim, key);
        TxFrame[] frames = [FrameTxTestFrames.SelfVerify(FrameTxTestFrames.PrefixFrameGas),
            new(FrameMode.DepVerify, FrameFlags.None, null, Eip8288Constants.LeanStarkVerificationGas,
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
