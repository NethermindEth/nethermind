// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.ProofAggregation;

public class MempoolWrapperTests
{
    private static readonly ILeanProofVerifier Accepting = new FakeLeanProofVerifier(true);

    private static FrameDependency Sphincs(string tag) =>
        new(Eip8288Constants.LeanSphincsScheme, Keccak.Compute(tag), Keccak.Compute(tag + "-vk"));

    private static FrameDependency Stark(string tag) =>
        new(Eip8288Constants.LeanStarkScheme, Keccak.Compute(tag), Keccak.Compute(tag + "-vk"));

    [Test]
    public void Direct_wrapper_round_trips_full_transactions()
    {
        MempoolWrapper wrapper = Batch(MempoolWrapper.ModeDirect, [[Sphincs("a")], [Sphincs("b")]]);

        MempoolWrapper decoded = RoundTrip(wrapper);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.Mode, Is.EqualTo(MempoolWrapper.ModeDirect));
            Assert.That(decoded.Transactions.Select(static tx => tx.Hash), Is.EqualTo(wrapper.Transactions.Select(static tx => tx.Hash)));
            Assert.That(decoded.Transactions.Select(MempoolWrapperDecoder.EncodeTransaction),
                Is.EqualTo(wrapper.Transactions.Select(MempoolWrapperDecoder.EncodeTransaction)));
            Assert.That(decoded.Deps, Is.EqualTo(wrapper.Deps));
            Assert.That(decoded.Proofs, Is.EqualTo(wrapper.Proofs));
            Assert.That(MempoolWrapperValidator.Validate(decoded, Accepting, Eip8288Verifier.Pinned, out string? error), Is.True, error);
        }
    }

    [Test]
    public void Recursive_wrapper_round_trips()
    {
        MempoolWrapper wrapper = Batch(MempoolWrapper.ModeRecursive, [[Sphincs("a")]]);

        MempoolWrapper decoded = RoundTrip(wrapper);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.Mode, Is.EqualTo(MempoolWrapper.ModeRecursive));
            Assert.That(decoded.RecursiveStark!.StarkProof, Is.EqualTo(wrapper.RecursiveStark!.StarkProof));
            Assert.That(decoded.RecursiveStark!.BlockDepsHash, Is.EqualTo(new Hash256(Eip8288Dependencies.ComputeDepsHash(wrapper.Deps))));
        }
    }

    [Test]
    public void Encodes_the_canonical_body()
    {
        MempoolWrapper wrapper = Batch(MempoolWrapper.ModeRecursive, [[Sphincs("a")]]);
        // EIP-8288: [transactions, mode, [deps, recursive_stark]], each transaction its full pooled encoding as a byte string.
        Rlp expected = Rlp.Encode(
            Rlp.Encode([Rlp.Encode(MempoolWrapperDecoder.EncodeTransaction(wrapper.Transactions[0]))]),
            Rlp.Encode(1UL),
            Rlp.Encode(Rlp.Encode([Rlp.Encode(Eip8288Dependencies.Serialize(wrapper.Deps))]), Rlp.Encode(wrapper.RecursiveStark!.StarkProof)));

        Assert.That(MempoolWrapperDecoder.Instance.Encode(wrapper).Bytes, Is.EqualTo(expected.Bytes));
    }

    [Test]
    public void Encoder_rejects_large_direct_witness_totals_before_allocating_output()
    {
        MempoolWrapper wrapper = Batch(MempoolWrapper.ModeDirect, [[Sphincs("a"), Sphincs("b")]]);
        wrapper = With(wrapper, proofs: [new byte[5 * 1024 * 1024], new byte[5 * 1024 * 1024]]);
        Assert.Throws<RlpException>(() => MempoolWrapperDecoder.Instance.GetLength(wrapper, RlpBehaviors.None));
    }

    [Test]
    public void Valid_wrappers_pass([Values(MempoolWrapper.ModeDirect, MempoolWrapper.ModeRecursive)] byte mode)
    {
        MempoolWrapper wrapper = Batch(mode, [[Sphincs("a"), Sphincs("b")], [Sphincs("c")]]);
        Assert.That(MempoolWrapperValidator.Validate(wrapper, Accepting, Eip8288Verifier.Pinned, out string? error), Is.True, error);
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(3)]
    public void Direct_wrapper_rejects_missing_or_extra_proofs(int proofs)
    {
        MempoolWrapper wrapper = Batch(MempoolWrapper.ModeDirect, [[Sphincs("a"), Sphincs("b")]]);
        wrapper = With(wrapper, proofs: [.. Enumerable.Repeat(new byte[] { 1 }, proofs)]);
        Assert.That(MempoolWrapperValidator.Validate(wrapper, Accepting, Eip8288Verifier.Pinned, out string? error), Is.False);
        Assert.That(error, Is.EqualTo(MempoolWrapperValidator.ProofCountMismatch));
    }

    [Test]
    public void Recursive_wrapper_rejects_deps_hash_mismatch()
    {
        MempoolWrapper wrapper = Batch(MempoolWrapper.ModeRecursive, [[Sphincs("a")]]);
        wrapper = With(wrapper, stark: new RecursiveStark([1], Keccak.Compute("wrong")));
        Assert.That(MempoolWrapperValidator.Validate(wrapper, Accepting, Eip8288Verifier.Pinned, out string? error), Is.False);
        Assert.That(error, Is.EqualTo(MempoolWrapperValidator.DepsHashMismatch));
    }

    // EIP-8288 Test Case 12: mode 0 accepts 16 signature dependencies. With 17 distinct dependencies across two individually
    // valid transactions, mode 0 is rejected and an otherwise valid mode 1 wrapper within MAX_WRAPPER_BYTES is accepted.
    [Test]
    public void Wrapper_dependency_limits_apply_to_mode_0_only(
        [Values(MempoolWrapper.ModeDirect, MempoolWrapper.ModeRecursive)] byte mode, [Values] bool exceeded)
    {
        MempoolWrapper wrapper = RoundTrip(Batch(mode, [Sphincses("a", 8), Sphincses("b", exceeded ? 9 : 8)]));
        FakeLeanProofVerifier verifier = new(true);
        bool valid = MempoolWrapperValidator.Validate(wrapper, verifier, Eip8288Verifier.Pinned, out string? error);
        bool rejected = exceeded && mode == MempoolWrapper.ModeDirect;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(valid, Is.EqualTo(!rejected), error);
            Assert.That(error, Is.EqualTo(rejected ? MempoolWrapperValidator.TooManySigDeps : null));
            if (rejected) Assert.That(verifier.VerificationCalls, Is.Zero, "limits are checked before verification");
        }
    }

    // Mode 1 has no wrapper-level dependency-count limit: MAX_WRAPPER_BYTES bounds it, and per-transaction limits still apply.
    [Test]
    public void Recursive_wrapper_may_exceed_the_mode_0_limits()
    {
        MempoolWrapper wrapper = RoundTrip(Batch(MempoolWrapper.ModeRecursive,
            [.. Enumerable.Range(0, 5).Select(i => Sphincses($"t{i}", Eip8288Constants.MaxSigsPerTx))]));
        Assert.That(wrapper.Deps, Has.Count.EqualTo(5 * Eip8288Constants.MaxSigsPerTx));
        Assert.That(MempoolWrapperValidator.Validate(wrapper, Accepting, Eip8288Verifier.Pinned, out string? error), Is.True, error);
    }

    // LEANSTARK_SCHEME is reserved: a transaction whose dependency frame uses it is rejected before verification.
    [Test]
    public void Wrapper_rejects_reserved_leanstark_deps([Values(MempoolWrapper.ModeDirect, MempoolWrapper.ModeRecursive)] byte mode)
    {
        MempoolWrapper wrapper = Batch(mode, [[Stark("a")]]);
        FakeLeanProofVerifier verifier = new(true);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(MempoolWrapperValidator.Validate(wrapper, verifier, Eip8288Verifier.Pinned, out string? error), Is.False);
            Assert.That(error, Is.EqualTo(MempoolWrapperValidator.MalformedDependencyFrame));
            Assert.That(verifier.VerificationCalls, Is.Zero);
        }
    }

    [Test]
    public void Per_transaction_limits_still_apply([Values(MempoolWrapper.ModeDirect, MempoolWrapper.ModeRecursive)] byte mode)
    {
        MempoolWrapper wrapper = Batch(mode, [Sphincses("a", Eip8288Constants.MaxSigsPerTx + 1)]);
        Assert.That(MempoolWrapperValidator.Validate(wrapper, Accepting, Eip8288Verifier.Pinned, out string? error), Is.False);
        Assert.That(error, Is.EqualTo(MempoolWrapperValidator.TransactionLimits));
    }

    // EIP-8437: exceeding a dependency limit is a dependency-rule failure, discarded without penalty, so decoding accepts it
    // and validation rejects it.
    [Test]
    public void Decoder_leaves_dependency_limits_to_validation([Values(MempoolWrapper.ModeDirect, MempoolWrapper.ModeRecursive)] byte mode)
    {
        FrameDependency[] deps = Sphincses("d", Eip8288Constants.MaxLeanSigDepsPerWrapper + 1);
        List<FrameDependency> canonical = Eip8288Dependencies.Canonicalize(deps);
        Rlp encoded = Raw([Encoded(Sphincses("t", 1))], mode, canonical,
            mode == MempoolWrapper.ModeDirect ? Rlp.Encode([.. canonical.Select(static _ => Rlp.Encode(new byte[] { 1 }))]) : Rlp.Encode(new byte[] { 1 }));
        MempoolWrapper decoded = Decode(encoded);
        Assert.That(decoded.Deps, Has.Count.EqualTo(deps.Length));
        Assert.That(MempoolWrapperValidator.Validate(decoded, Accepting, Eip8288Verifier.Pinned, out _), Is.False);
    }

    [Test]
    public void Decoder_leaves_dependency_order_and_witness_count_to_validation([Values] bool unsorted)
    {
        List<FrameDependency> canonical = Eip8288Dependencies.Canonicalize([Sphincs("a"), Sphincs("b")]);
        Rlp encoded = unsorted
            ? Raw([Encoded(canonical)], MempoolWrapper.ModeDirect, [canonical[1], canonical[0]],
                Rlp.Encode([Rlp.Encode(new byte[] { 1 }), Rlp.Encode(new byte[] { 1 })]))
            : Raw([Encoded(canonical)], MempoolWrapper.ModeDirect, canonical, Rlp.Encode([Rlp.Encode(new byte[] { 1 })]));
        MempoolWrapper decoded = Decode(encoded);
        Assert.That(MempoolWrapperValidator.Validate(decoded, Accepting, Eip8288Verifier.Pinned, out string? error), Is.False);
        Assert.That(error, Is.EqualTo(unsorted ? MempoolWrapperValidator.DepsMismatch : MempoolWrapperValidator.ProofCountMismatch));
    }

    [Test]
    public void Duplicate_dependencies_are_rejected()
    {
        FrameDependency dependency = Sphincs("a");
        MempoolWrapper wrapper = Batch(MempoolWrapper.ModeDirect, [[dependency]]);
        wrapper = new MempoolWrapper { Transactions = wrapper.Transactions, Mode = MempoolWrapper.ModeDirect, Deps = [dependency, dependency], Proofs = [[1], [1]] };
        Assert.That(MempoolWrapperValidator.Validate(wrapper, Accepting, Eip8288Verifier.Pinned, out string? error), Is.False);
        Assert.That(error, Is.EqualTo(MempoolWrapperValidator.DepsMismatch));
    }

    // EIP-8288 Test Case 12: the dependency union must match dependencies(tx), including the zero-hash substitution.
    [Test]
    public void Dependency_union_must_match_the_transactions()
    {
        MempoolWrapper wrapper = Batch(MempoolWrapper.ModeRecursive, [[Sphincs("declared")]]);
        List<FrameDependency> claimed = [Sphincs("other")];
        wrapper = With(wrapper, deps: claimed, stark: new RecursiveStark([1], new Hash256(Eip8288Dependencies.ComputeDepsHash(claimed))));
        FakeLeanProofVerifier verifier = new(true);
        Assert.That(MempoolWrapperValidator.Validate(wrapper, verifier, Eip8288Verifier.Pinned, out string? error), Is.False);
        Assert.That(error, Is.EqualTo(MempoolWrapperValidator.DepsMismatch));
        Assert.That(verifier.VerificationCalls, Is.Zero);
    }

    [Test]
    public void Deps_blob_that_is_not_a_multiple_of_the_triple_length_is_rejected()
    {
        Rlp encoded = Rlp.Encode(
            Rlp.Encode([Rlp.Encode(Encoded([Sphincs("a")]))]),
            Rlp.Encode((ulong)MempoolWrapper.ModeDirect),
            Rlp.Encode(Rlp.Encode([Rlp.Encode(new byte[Eip8288Constants.DependencyTripleLength + 1])]), Rlp.Encode(Array.Empty<Rlp>())));
        Assert.That(() => Decode(encoded), Throws.InstanceOf<RlpException>());
    }

    [Test]
    public void Recursive_wrapper_rejects_extra_inner_values()
    {
        List<FrameDependency> deps = [Sphincs("a")];
        Rlp encoded = Rlp.Encode(
            Rlp.Encode([Rlp.Encode(Encoded(deps))]),
            Rlp.Encode((ulong)MempoolWrapper.ModeRecursive),
            Rlp.Encode(Rlp.Encode([Rlp.Encode(Eip8288Dependencies.Serialize(deps))]), Rlp.Encode(new byte[] { 1 }), Rlp.Encode(Keccak.Compute("deps"))));
        Assert.That(() => Decode(encoded), Throws.InstanceOf<RlpException>());
    }

    [Test]
    public void Wrapper_rejects_noncanonical_scheme_word([Values] bool unknownScheme)
    {
        byte[] dependency = new byte[Eip8288Constants.DependencyTripleLength];
        dependency[31] = unknownScheme ? (byte)0xff : Eip8288Constants.LeanSphincsScheme;
        if (!unknownScheme) dependency[0] = 1;
        Rlp encoded = Rlp.Encode(Rlp.Encode([Rlp.Encode(Encoded([Sphincs("a")]))]), Rlp.Encode(0UL),
            Rlp.Encode(Rlp.Encode([Rlp.Encode(dependency)]), Rlp.Encode([Rlp.Encode(new byte[] { 1 })])));
        Assert.That(() => Decode(encoded), Throws.InstanceOf<RlpException>());
    }

    // EIP-8288 Test Case 12: empty transaction lists, unresolved hash references, non-frame or malformed transactions and
    // duplicate or out-of-order transaction hashes are rejected.
    [TestCase("hash-reference")]
    [TestCase("raw-hash")]
    [TestCase("legacy")]
    [TestCase("unsorted")]
    [TestCase("duplicate")]
    [TestCase("empty")]
    [TestCase("short-dependency")]
    public void Decode_rejects_malformed_transaction_lists(string scenario)
    {
        byte[][] transactions = [.. new[] { Encoded([Sphincs("a")], nonce: 0), Encoded([Sphincs("a")], nonce: 1) }
            .OrderBy(static tx => ValueKeccak.Compute(tx).ToString())];
        Rlp[] entries = scenario switch
        {
            "hash-reference" => [Rlp.Encode(Rlp.Encode(Keccak.Compute(transactions[0])))],
            "raw-hash" => [Rlp.Encode(Keccak.Compute(transactions[0]))],
            "legacy" => [Rlp.Encode(TxDecoder.Instance.Encode(Build.A.Transaction.SignedAndResolved().TestObject).Bytes)],
            "unsorted" => [Rlp.Encode(transactions[1]), Rlp.Encode(transactions[0])],
            "duplicate" => [Rlp.Encode(transactions[0]), Rlp.Encode(transactions[0])],
            "empty" => [],
            _ => [Rlp.Encode(transactions[0])]
        };
        Rlp deps = Rlp.Encode(scenario == "short-dependency"
            ? [Rlp.Encode(new byte[Eip8288Constants.DependencyTripleLength - 1])]
            : [Rlp.Encode(Eip8288Dependencies.Serialize([Sphincs("a")]))]);
        Rlp encoded = Rlp.Encode(Rlp.Encode(entries), Rlp.Encode(0UL), Rlp.Encode(deps, Rlp.Encode([Rlp.Encode(new byte[] { 1 })])));

        Assert.That(() => Decode(encoded), Throws.InstanceOf<RlpException>());
    }

    [Test]
    public void Excessive_witness_entries_are_a_proof_rule_failure()
    {
        Rlp encoded = Raw([Encoded([Sphincs("a")])], MempoolWrapper.ModeDirect, [Sphincs("a")],
            Rlp.Encode([.. Enumerable.Range(0, Eip8288Constants.MaxLeanSigDepsPerWrapper + 2).Select(static _ => Rlp.Encode(new byte[] { 1 }))]));
        Assert.That(MempoolWrapperValidator.Validate(Decode(encoded), Accepting, Eip8288Verifier.Pinned, out string? error), Is.False);
        Assert.That(error, Is.EqualTo(MempoolWrapperValidator.ProofCountMismatch));
    }

    // EIP-8288 Test Case 12: MAX_WRAPPER_BYTES bounds the complete encoding, full transactions included: a wrapper exactly at
    // the limit is accepted and one byte above it is rejected.
    [Test]
    public void Max_wrapper_bytes_bounds_the_complete_encoding()
    {
        List<FrameDependency> deps = [Sphincs("a")];
        byte[] proof = new byte[1024];
        int payload = Eip8288Constants.MaxWrapperBytes - 4096;
        Rlp atLimit = Raw([Encoded(deps, payload: payload)], MempoolWrapper.ModeRecursive, deps, Rlp.Encode(proof));
        for (int i = 0; i < 4 && atLimit.Length != Eip8288Constants.MaxWrapperBytes; i++)
        {
            payload += Eip8288Constants.MaxWrapperBytes - atLimit.Length;
            atLimit = Raw([Encoded(deps, payload: payload)], MempoolWrapper.ModeRecursive, deps, Rlp.Encode(proof));
        }
        Rlp oversized = Raw([Encoded(deps, payload: payload + 1)], MempoolWrapper.ModeRecursive, deps, Rlp.Encode(proof));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(atLimit.Length, Is.EqualTo(Eip8288Constants.MaxWrapperBytes));
            Assert.That(oversized.Length, Is.EqualTo(Eip8288Constants.MaxWrapperBytes + 1));
            Assert.That(Decode(atLimit).Transactions, Has.Count.EqualTo(1));
            Assert.That(MempoolWrapperDecoder.Instance.Encode(Decode(atLimit)).Bytes, Is.EqualTo(atLimit.Bytes));
            Assert.That(() => Decode(oversized), Throws.InstanceOf<RlpException>());
        }
    }

    // EIP-8288 Test Case 12: every listed transaction declares a dependency, even when another one makes deps nonempty.
    [Test]
    public void Listed_transaction_without_dependencies_is_rejected([Values(MempoolWrapper.ModeDirect, MempoolWrapper.ModeRecursive)] byte mode)
    {
        MempoolWrapper batch = Batch(mode, [[Sphincs("a")], []]);
        FakeLeanProofVerifier verifier = new(true);
        bool valid = MempoolWrapperValidator.Validate(batch, verifier, Eip8288Verifier.Pinned, out string? error);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(valid, Is.False);
            Assert.That(error, Is.EqualTo(MempoolWrapperValidator.TransactionWithoutDependencies));
            Assert.That(verifier.VerificationCalls, Is.Zero);
        }
    }

    [Test]
    public void Malformed_dependency_frame_is_rejected([Values(MempoolWrapper.ModeDirect, MempoolWrapper.ModeRecursive)] byte mode)
    {
        MempoolWrapper batch = Batch(mode, [[Sphincs("a")]]);
        Transaction transaction = DepTx([Sphincs("b")], nonce: 9);
        transaction.Frames = [new TxFrame(FrameMode.DepVerify, FrameFlags.None, null, Eip8288Constants.LeanSphincsVerificationGas, UInt256.Zero, Array.Empty<byte>())];
        transaction.Hash = transaction.CalculateHash();
        MempoolWrapper wrapper = With(batch, transactions: Sorted([batch.Transactions[0], transaction]));
        Assert.That(MempoolWrapperValidator.Validate(wrapper, Accepting, Eip8288Verifier.Pinned, out string? error), Is.False);
        Assert.That(error, Is.EqualTo(MempoolWrapperValidator.MalformedDependencyFrame));
    }

    // A mode-1 proof above the local MaxAggregateProofBytes bound is rejected before verification.
    [TestCase(Eip8288Constants.MaxAggregateProofBytes, null, 1)]
    [TestCase(Eip8288Constants.MaxAggregateProofBytes + 1, MempoolWrapperValidator.InvalidProof, 0)]
    public void Aggregate_proof_size_is_bounded_before_verification(int length, string? expected, int calls)
    {
        MempoolWrapper batch = Batch(MempoolWrapper.ModeRecursive, [[Sphincs("a")]]);
        MempoolWrapper wrapper = With(batch, stark: new RecursiveStark(new byte[length], batch.RecursiveStark!.BlockDepsHash));
        FakeLeanProofVerifier verifier = new(true);
        bool valid = MempoolWrapperValidator.Validate(wrapper, verifier, Eip8288Verifier.Pinned, out string? error);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(valid, Is.EqualTo(expected is null), error);
            Assert.That(error, Is.EqualTo(expected));
            Assert.That(verifier.VerificationCalls, Is.EqualTo(calls));
        }
    }

    [Test]
    public void Wrapper_transactions_must_be_frame_transactions()
    {
        MempoolWrapper batch = Batch(MempoolWrapper.ModeDirect, [[Sphincs("a")]]);
        MempoolWrapper wrapper = With(batch, transactions: [Build.A.Transaction.SignedAndResolved().TestObject]);
        Assert.That(MempoolWrapperValidator.Validate(wrapper, Accepting, Eip8288Verifier.Pinned, out string? error), Is.False);
        Assert.That(error, Is.EqualTo(MempoolWrapperValidator.NotFrameTransaction));
    }

    // Verdicts are cached locally by proof, dependencies and AGGREGATED_VK, so the same proof arriving with another
    // transaction list is not verified again, whichever verdict it had.
    [Test]
    public void Known_proof_verdict_is_reused_for_another_transaction_list(
        [Values(MempoolWrapper.ModeDirect, MempoolWrapper.ModeRecursive)] byte mode, [Values] bool valid)
    {
        MempoolWrapper first = Batch(mode, [[Sphincs("reuse-a"), Sphincs("reuse-b")]]);
        MempoolWrapper second = Batch(mode, [[Sphincs("reuse-a")], [Sphincs("reuse-b")]]);
        FakeLeanProofVerifier verifier = new(valid);
        bool firstValid = MempoolWrapperValidator.VerifyProofs(first, verifier, Eip8288Verifier.Pinned, out _);
        int calls = verifier.VerificationCalls;
        bool secondValid = MempoolWrapperValidator.VerifyProofs(second, verifier, Eip8288Verifier.Pinned, out _);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstValid, Is.EqualTo(valid));
            Assert.That(secondValid, Is.EqualTo(valid));
            Assert.That(calls, Is.EqualTo(mode == MempoolWrapper.ModeDirect && valid ? 2 : 1));
            Assert.That(verifier.VerificationCalls, Is.EqualTo(calls));
        }
    }

    // A zero data_hash stands for the containing transaction's signature hash, so a mode-0 witness is checked against the
    // signature hash, not the zero placeholder.
    [Test]
    public void Direct_witness_for_a_zero_data_hash_verifies_against_the_signature_hash()
    {
        Transaction transaction = DepTx([new FrameDependency(Eip8288Constants.LeanSphincsScheme, default, TestItem.KeccakA.ValueHash256)]);
        List<FrameDependency> deps = Eip8288Dependencies.ForTransaction(transaction, FrameTxSigHash.ComputeValue);
        MempoolWrapper wrapper = new() { Transactions = [transaction], Mode = MempoolWrapper.ModeDirect, Deps = deps, Proofs = [[1]] };
        ValueHash256? checkedHash = null;
        DataHashVerifier verifier = new(dataHash => checkedHash = dataHash);
        bool valid = MempoolWrapperValidator.Validate(wrapper, verifier, Eip8288Verifier.Pinned, out string? error);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(valid, Is.True, error);
            Assert.That(deps.Single().DataHash, Is.EqualTo(FrameTxSigHash.ComputeValue(transaction)));
            Assert.That(checkedHash, Is.EqualTo(FrameTxSigHash.ComputeValue(transaction)));
        }
    }

    /// <summary>Accepts every leanSPHINCS witness, reporting the statement it was checked against.</summary>
    private sealed class DataHashVerifier(Action<ValueHash256> onVerify) : ILeanProofVerifier
    {
        public void EnsureAvailable() { }
        public bool VerifyLeanSphincs(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness)
        {
            onVerify(dataHash);
            return true;
        }
        public bool VerifyRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, ReadOnlySpan<byte> proof) => false;
        public byte[] ProveRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, AggregationInput input) => [];
    }

    /// <summary>A frame transaction declaring <paramref name="deps"/> in one dependency verification frame.</summary>
    internal static Transaction DepTx(IReadOnlyList<FrameDependency> deps, ulong nonce = 0, int payload = 0)
    {
        Transaction transaction = new()
        {
            Type = TxType.FrameTx,
            ChainId = 1,
            SenderAddress = Address.Zero,
            NonceKeys = [UInt256.Zero],
            Nonce = nonce,
            Frames =
            [
                .. deps.Count == 0 ? Array.Empty<TxFrame>() : [new TxFrame(FrameMode.DepVerify, FrameFlags.None, null,
                    Eip8288Constants.LeanSphincsVerificationGas, UInt256.Zero, Eip8288Dependencies.Serialize(deps))],
                .. payload == 0 && deps.Count != 0 ? Array.Empty<TxFrame>()
                    : [new TxFrame(FrameMode.Sender, FrameFlags.None, Address.Zero, 100_000, UInt256.Zero, new byte[payload])]
            ]
        };
        transaction.Hash = transaction.CalculateHash();
        return transaction;
    }

    private static byte[] Encoded(IReadOnlyList<FrameDependency> deps, ulong nonce = 0, int payload = 0) =>
        MempoolWrapperDecoder.EncodeTransaction(DepTx(deps, nonce, payload));

    private static Transaction[] Sorted(IEnumerable<Transaction> transactions) =>
        [.. transactions.OrderBy(static tx => tx.Hash!.ToString())];

    /// <summary>A wrapper over one transaction per entry of <paramref name="transactionDeps"/>.</summary>
    private static MempoolWrapper Batch(byte mode, IReadOnlyList<FrameDependency>[] transactionDeps)
    {
        Transaction[] transactions = Sorted(transactionDeps.Select(static (deps, i) => DepTx(deps, (ulong)i)));
        List<FrameDependency> deps = Eip8288Dependencies.Canonicalize(
            transactions.SelectMany(static tx => Eip8288Dependencies.ForTransaction(tx, FrameTxSigHash.ComputeValue)));
        return new MempoolWrapper
        {
            Transactions = transactions,
            Mode = mode,
            Deps = deps,
            Proofs = mode == MempoolWrapper.ModeDirect ? [.. deps.Select(static _ => new byte[] { 1 })] : null,
            RecursiveStark = mode == MempoolWrapper.ModeRecursive
                ? new RecursiveStark([1], new Hash256(Eip8288Dependencies.ComputeDepsHash(deps)))
                : null
        };
    }

    private static MempoolWrapper With(MempoolWrapper wrapper, IReadOnlyList<Transaction>? transactions = null,
        IReadOnlyList<FrameDependency>? deps = null, IReadOnlyList<byte[]>? proofs = null, RecursiveStark? stark = null) => new()
        {
            Transactions = transactions ?? wrapper.Transactions,
            Mode = wrapper.Mode,
            Deps = deps ?? wrapper.Deps,
            Proofs = proofs ?? wrapper.Proofs,
            RecursiveStark = stark ?? wrapper.RecursiveStark
        };

    private static FrameDependency[] Sphincses(string prefix, int count) =>
        [.. Enumerable.Range(0, count).Select(i => Sphincs($"{prefix}{i}"))];

    private static Rlp Raw(byte[][] transactions, byte mode, IReadOnlyList<FrameDependency> deps, Rlp proofContent) =>
        Rlp.Encode(
            Rlp.Encode([.. transactions.Select(static tx => Rlp.Encode(tx))]),
            Rlp.Encode((ulong)mode),
            Rlp.Encode(Rlp.Encode([.. Enumerable.Range(0, deps.Count)
                .Select(i => Rlp.Encode(Eip8288Dependencies.Serialize(deps).AsSpan(i * Eip8288Constants.DependencyTripleLength, Eip8288Constants.DependencyTripleLength)))]),
                proofContent));

    private static MempoolWrapper Decode(Rlp encoded)
    {
        RlpReader reader = new(encoded.Bytes);
        MempoolWrapper wrapper = MempoolWrapperDecoder.Instance.Decode(ref reader)!;
        reader.Check(encoded.Length);
        return wrapper;
    }

    private static MempoolWrapper RoundTrip(MempoolWrapper wrapper) => Decode(MempoolWrapperDecoder.Instance.Encode(wrapper));
}
