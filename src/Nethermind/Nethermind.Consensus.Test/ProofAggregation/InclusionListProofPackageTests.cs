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
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.ProofAggregation;

public class InclusionListProofPackageTests
{
    private static readonly ILeanProofVerifier Accepting = new FakeLeanProofVerifier(true);

    private static Transaction DepTx(byte scheme)
    {
        byte[] data = new byte[Eip8288Constants.DependencyTripleLength];
        data[31] = scheme;
        return new Transaction
        {
            Type = TxType.FrameTx,
            Frames = [new TxFrame(FrameMode.DepVerify, FrameFlags.None, null, 0, UInt256.Zero, data)],
        };
    }

    [Test]
    public void Shared_crypto_verdict_uses_exact_bytes_without_retaining_mutable_proofs()
    {
        FakeLeanProofVerifier verifier = new(true);
        Transaction transaction = DepTx(Eip8288Constants.LeanSphincsScheme);
        List<FrameDependency> dependencies = Eip8288Dependencies.ForTransaction(transaction, FrameTxSigHash.ComputeValue);
        RecursiveStark proof = new([1], new Hash256(Eip8288Dependencies.ComputeDepsHash(dependencies)));
        Assert.That(InclusionListProofValidator.Validate([transaction], proof, verifier, out _, out _), Is.True);
        Assert.That(InclusionListProofValidator.Validate([transaction], proof, verifier, out _, out _), Is.True);
        Assert.That(verifier.VerificationCalls, Is.EqualTo(1));
        proof.StarkProof[0] = 2;
        Assert.That(InclusionListProofValidator.Validate([transaction], proof, verifier, out _, out _), Is.True);
        Assert.That(verifier.VerificationCalls, Is.EqualTo(2));
    }

    // EIPs#12473/#12475: verdicts are cached by (keccak256(proof), deps_hash), both positive and negative, so a known proof
    // arriving in another list is not verified again; other bytes or another deps_hash are verified afresh.
    [Test]
    public void Verdict_cache_keys_on_proof_hash_and_deps_hash([Values] bool valid)
    {
        FakeLeanProofVerifier backend = new(valid);
        ValueHash256 deps = ValueKeccak.Compute([1]);
        byte[] proof = [1];
        for (int check = 0; check < 4; check++) Assert.That(ProofVerdictCache.VerifyRecursive(backend, in deps, proof), Is.EqualTo(valid));
        Assert.That(backend.VerificationCalls, Is.EqualTo(1));
        proof[0] = 2;
        Assert.That(ProofVerdictCache.VerifyRecursive(backend, in deps, proof), Is.EqualTo(valid));
        Assert.That(backend.VerificationCalls, Is.EqualTo(2));
        deps = ValueKeccak.Compute([2]);
        Assert.That(ProofVerdictCache.VerifyRecursive(backend, in deps, proof), Is.EqualTo(valid));
        Assert.That(backend.VerificationCalls, Is.EqualTo(3));
    }

    [Test]
    public void Verdict_cache_is_bounded()
    {
        FakeLeanProofVerifier backend = new(true);
        ValueHash256 deps = ValueKeccak.Compute([1]);
        for (int i = 0; i <= ProofVerdictCache.Capacity; i++)
            ProofVerdictCache.VerifyRecursive(backend, in deps, BitConverter.GetBytes(i));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ProofVerdictCache.For(backend).Count, Is.EqualTo(ProofVerdictCache.Capacity));
            // The oldest verdict was evicted, so its proof is verified again.
            ProofVerdictCache.VerifyRecursive(backend, in deps, BitConverter.GetBytes(0));
            Assert.That(backend.VerificationCalls, Is.EqualTo(ProofVerdictCache.Capacity + 2));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Fork_invalid_frame_envelopes_are_rejected_before_proof_verification(bool recentRoots)
    {
        OverridableReleaseSpec spec = new(Eip8288Prototype.Instance) { IsEip8272Enabled = !recentRoots };
        FrameDependency dependency = new(Eip8288Constants.LeanSphincsScheme, default, default);
        Transaction transaction = new()
        {
            Type = TxType.FrameTx,
            SenderAddress = Address.Zero,
            NonceKeys = recentRoots ? [UInt256.Zero] : null,
            RecentRootReferences = recentRoots ? [new(default, 0, default)] : null,
            Frames = [new(FrameMode.DepVerify, FrameFlags.None, null, Eip8288Constants.LeanSphincsVerificationGas,
                UInt256.Zero, Eip8288Dependencies.Serialize([dependency]))]
        };
        FakeLeanProofVerifier verifier = new(true);
        RecursiveStark proof = new([1], new Hash256(Eip8288Dependencies.ComputeDepsHash([dependency])));

        bool valid = InclusionListProofValidator.Validate([transaction], proof, verifier, out _, out string? error, spec);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(valid, Is.False);
            Assert.That(error, Is.EqualTo(recentRoots ? FrameTxValidation.RecentRootReferencesNotEnabled : FrameTxValidation.LegacyNonceNotAllowed));
            Assert.That(verifier.VerificationCalls, Is.Zero);
        }
    }

    // EIPs#12475: obligations follow from the signed commitment alone. A committed list binds every statically valid entry,
    // whatever its proof; a null commitment binds only dependency-free entries. Only a proof that verifies against deps over
    // the statically valid entries is aggregated; a malformed entry contributes no dependencies.
    [TestCase("malformed")]
    [TestCase("unverifiable-proof")]
    [TestCase("null-commitment")]
    [TestCase("empty-proof")]
    [TestCase("proof-for-empty-dependencies")]
    [TestCase("valid")]
    public void Eligibility_follows_the_commitment_and_preserves_independent_entries(string scenario)
    {
        Transaction ordinary = new();
        Transaction covered = ValidDependencyFrame(default);
        Transaction other = ValidDependencyFrame(ValueKeccak.Compute("other"));
        if (scenario == "malformed") other.Frames = [new(FrameMode.DepVerify, FrameFlags.None, null, 0, UInt256.Zero, new byte[1])];
        Transaction[] transactions = scenario == "proof-for-empty-dependencies" ? [ordinary] : [covered, other, ordinary];
        byte[]? proof = scenario switch
        {
            "null-commitment" => null,
            "empty-proof" => [],
            _ => [1]
        };
        FakeLeanProofVerifier verifier = new(scenario != "unverifiable-proof");
        CommitteeInclusionList[] lists = [new(0, transactions, proof)];

        Transaction[] eligible = InclusionListProofValidator.SelectEligible([], lists, Eip8288Prototype.Instance);
        List<RecursiveProofInput> proven = InclusionListProofValidator.ProvenInputs(lists, verifier, Eip8288Prototype.Instance,
            out List<ulong> unusable);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(eligible, scenario switch
            {
                "malformed" => Is.EqualTo(new[] { covered, ordinary }),
                "null-commitment" or "proof-for-empty-dependencies" => Is.EqualTo(new[] { ordinary }),
                _ => Is.EqualTo(new[] { covered, other, ordinary })
            });
            Assert.That(proven, Has.Count.EqualTo(scenario is "malformed" or "valid" ? 1 : 0));
            if (scenario == "malformed")
                Assert.That(proven[0].InnerDeps, Is.EqualTo(Eip8288Dependencies.ForTransaction(covered, FrameTxSigHash.ComputeValue)));
            Assert.That(unusable, Is.EqualTo(scenario is "unverifiable-proof" or "empty-proof" ? new ulong[] { 0 } : []));
            Assert.That(verifier.VerificationCalls, Is.EqualTo(scenario is "malformed" or "valid" or "unverifiable-proof" ? 1 : 0));
        }
    }

    // An independent committed list can require the same transaction, and each list's proof is aggregated on its own.
    [Test]
    public void Each_committed_list_binds_its_entries_whatever_its_proof()
    {
        Transaction ordinary = new() { Hash = Keccak.Compute("ordinary") };
        Transaction shared = ValidDependencyFrame(ValueKeccak.Compute("shared"));
        Transaction onlyInvalid = ValidDependencyFrame(ValueKeccak.Compute("only-invalid"));
        List<FrameDependency> sharedDeps = Eip8288Dependencies.ForTransaction(shared, FrameTxSigHash.ComputeValue);
        byte[] validProof = [1];
        ProofBytesVerifier verifier = new(valid: validProof);
        CommitteeInclusionList[] lists = [new(2, [shared, onlyInvalid, ordinary], [2]), new(9, [shared], validProof)];

        Transaction[] eligible = InclusionListProofValidator.SelectEligible([], lists, Eip8288Prototype.Instance);
        List<RecursiveProofInput> proven = InclusionListProofValidator.ProvenInputs(lists, verifier, Eip8288Prototype.Instance,
            out List<ulong> unusable);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(eligible, Is.EqualTo(new[] { shared, onlyInvalid, ordinary }));
            Assert.That(proven, Has.Count.EqualTo(1));
            Assert.That(proven[0].InnerDeps, Is.EqualTo(sharedDeps));
            Assert.That(proven[0].Proof.ToArray(), Is.EqualTo(validProof));
            Assert.That(unusable, Is.EqualTo(new ulong[] { 2 }));
        }
    }

    [Test]
    public void Without_per_list_input_only_dependency_free_entries_bind()
    {
        Transaction ordinary = new();
        Transaction covered = ValidDependencyFrame(default);
        Assert.That(InclusionListProofValidator.SelectEligible([covered, ordinary], null, Eip8288Prototype.Instance),
            Is.EqualTo(new[] { ordinary }));
    }

    // engine_verifyInclusionListProofV1 semantics: deps over the statically valid entries, deps_hash must match, the proof
    // must be nonempty, bounded and verify. A zero data_hash resolves to the signature hash for every scheme.
    [TestCase("valid", null)]
    [TestCase("wrong-deps-hash", InclusionListProofValidator.DepsHashMismatch)]
    [TestCase("invalid-proof", InclusionListProofValidator.InvalidProof)]
    [TestCase("empty-proof", InclusionListProofValidator.EmptyProof)]
    [TestCase("oversized-proof", InclusionListProofValidator.OversizedProof)]
    [TestCase("no-dependencies", InclusionListProofValidator.EmptyDependencies)]
    [TestCase("malformed-entry-ignored", null)]
    public void Commitment_verification(string scenario, string? expectedError)
    {
        Transaction covered = ValidDependencyFrame(default, Eip8288Constants.LeanStarkScheme);
        Transaction malformed = ValidDependencyFrame(ValueKeccak.Compute("malformed"));
        malformed.Frames = [new(FrameMode.DepVerify, FrameFlags.None, null, 0, UInt256.Zero, new byte[1])];
        Transaction[] transactions = scenario switch
        {
            "no-dependencies" => [new Transaction()],
            "malformed-entry-ignored" => [covered, malformed],
            _ => [covered]
        };
        List<FrameDependency> deps = Eip8288Dependencies.ForTransaction(covered, FrameTxSigHash.ComputeValue);
        ValueHash256 depsHash = scenario == "wrong-deps-hash" ? ValueKeccak.Compute("wrong") : Eip8288Dependencies.ComputeDepsHash(deps);
        byte[] proof = scenario switch
        {
            "empty-proof" => [],
            "oversized-proof" => new byte[Eip8288Constants.MaxAggregateProofBytes + 1],
            _ => [1]
        };
        FakeLeanProofVerifier verifier = new(scenario != "invalid-proof");

        bool valid = InclusionListProofValidator.VerifyCommitment(transactions, in depsHash, proof, verifier, Eip8288Prototype.Instance,
            out string? error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(valid, Is.EqualTo(expectedError is null));
            Assert.That(error, Is.EqualTo(expectedError));
            // The leanSTARK entry's zero data_hash stands for its signature hash in the committed digest.
            Assert.That(deps.Single().DataHash, Is.EqualTo(FrameTxSigHash.ComputeValue(covered)));
            Assert.That(verifier.VerificationCalls, Is.EqualTo(scenario is "valid" or "invalid-proof" or "malformed-entry-ignored" ? 1 : 0));
        }
    }

    /// <summary>Accepts exactly the proofs whose bytes are listed.</summary>
    private sealed class ProofBytesVerifier(byte[] valid) : ILeanProofVerifier
    {
        public void EnsureAvailable() { }
        public bool VerifyLeanSphincs(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness) => false;
        public bool VerifyLeanStark(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness) => false;
        public bool VerifyRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, ReadOnlySpan<byte> proof) => proof.SequenceEqual(valid);
        public byte[] ProveRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, AggregationInput input) => [];
    }

    private static Transaction ValidDependencyFrame(ValueHash256 message, byte scheme = Eip8288Constants.LeanSphincsScheme)
    {
        FrameDependency dep = new(scheme, message, default);
        TxFrame[] frames = [new(FrameMode.DepVerify, FrameFlags.None, null, dep.VerificationGas,
            UInt256.Zero, Eip8288Dependencies.Serialize([dep]))];
        return new()
        {
            Type = TxType.FrameTx,
            NonceKeys = [0],
            SenderAddress = Address.Zero,
            Frames = frames,
            GasLimit = FrameTxValidation.TotalGasLimit(frames)
        };
    }

    [Test]
    public void Empty_focil_round_trips()
    {
        InclusionListProofPackage focil = new()
        {
            Transactions = [],
            RecursiveStark = new RecursiveStark([1, 2, 3], Keccak.Compute("deps")),
        };

        Rlp rlp = InclusionListProofPackageDecoder.Instance.Encode(focil);
        RlpReader reader = new(rlp.Bytes);
        InclusionListProofPackage decoded = InclusionListProofPackageDecoder.Instance.Decode(ref reader)!;

        Assert.That(decoded.Transactions.Count, Is.EqualTo(0));
        Assert.That(decoded.RecursiveStark.StarkProof, Is.EqualTo(new byte[] { 1, 2, 3 }));
        Assert.That(decoded.RecursiveStark.BlockDepsHash, Is.EqualTo(Keccak.Compute("deps")));
    }

    [Test]
    public void Valid_when_recursive_stark_commits_to_all_dependencies()
    {
        List<Transaction> txs = [DepTx(Eip8288Constants.LeanSphincsScheme), DepTx(Eip8288Constants.LeanStarkScheme)];
        List<FrameDependency> deps = [];
        foreach (Transaction tx in txs) deps.AddRange(Eip8288Dependencies.ForTransaction(tx, FrameTxSigHash.ComputeValue));

        InclusionListProofPackage focil = new()
        {
            Transactions = txs,
            RecursiveStark = new RecursiveStark([1], new Hash256(Eip8288Dependencies.ComputeDepsHash(deps))),
        };

        Assert.That(InclusionListProofValidator.Validate(focil, Accepting, out List<FrameDependency> proven, out string? error), Is.True, error);
        Assert.That(proven, Is.EqualTo(Eip8288Dependencies.Canonicalize(deps)));
    }

    [TestCase("wrong")]
    [TestCase("superset")]
    public void Rejects_deps_hash_mismatch(string scenario)
    {
        Transaction transaction = DepTx(Eip8288Constants.LeanSphincsScheme);
        List<FrameDependency> superset = [.. Eip8288Dependencies.ForTransaction(transaction, FrameTxSigHash.ComputeValue), .. Eip8288Dependencies.ForTransaction(DepTx(Eip8288Constants.LeanStarkScheme), FrameTxSigHash.ComputeValue)];
        FakeLeanProofVerifier verifier = new(true);
        InclusionListProofPackage focil = new()
        {
            Transactions = [transaction],
            RecursiveStark = new RecursiveStark([1], scenario == "wrong" ? Keccak.Compute("wrong") : new Hash256(Eip8288Dependencies.ComputeDepsHash(superset))),
        };

        Assert.That(InclusionListProofValidator.Validate(focil, verifier, out _, out string? error), Is.False);
        Assert.That(error, Is.EqualTo(InclusionListProofValidator.DepsHashMismatch));
        Assert.That(verifier.VerificationCalls, Is.Zero);
    }

    // EIPs#12475: a list without dependencies carries a null commitment, so no package proof is valid for it.
    [Test]
    public void Empty_dependencies_admit_no_proof([Values] bool emptyProof)
    {
        FakeLeanProofVerifier verifier = new(true);
        InclusionListProofPackage focil = new()
        {
            Transactions = [new Transaction()],
            RecursiveStark = new RecursiveStark(emptyProof ? [] : [1], new Hash256(Eip8288Dependencies.ComputeDepsHash([]))),
        };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(InclusionListProofValidator.Validate(focil, verifier, out _, out string? error), Is.False);
            Assert.That(error, Is.EqualTo(InclusionListProofValidator.EmptyDependencies));
            Assert.That(verifier.VerificationCalls, Is.Zero);
        }
    }

    [Test]
    public void Decoder_rejects_extra_recursive_stark_fields()
    {
        Rlp encoded = Rlp.Encode(Rlp.Encode(System.Array.Empty<Rlp>()),
            Rlp.Encode(Rlp.Encode(new byte[] { 1 }), Rlp.Encode(Keccak.Zero), Rlp.Encode(new byte[Eip8288Constants.DependencyTripleLength])));
        Assert.Throws<RlpException>(() =>
        {
            RlpReader reader = new(encoded.Bytes);
            InclusionListProofPackageDecoder.Instance.Decode(ref reader);
        });
    }

    [Test]
    public void Typed_transactions_use_canonical_inclusion_list_encoding()
    {
        Transaction tx = Build.A.Transaction.WithType(TxType.EIP1559).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        InclusionListProofPackage list = new() { Transactions = [tx], RecursiveStark = new([1], Keccak.Zero) };
        Rlp encoded = InclusionListProofPackageDecoder.Instance.Encode(list);
        RlpReader reader = new(encoded.Bytes);
        reader.ReadSequenceLength();
        reader.ReadSequenceLength();
        byte[] entry = reader.DecodeByteArray();
        Assert.That(entry[0], Is.EqualTo((byte)TxType.EIP1559));
        reader = new(encoded.Bytes);
        Assert.That(InclusionListProofPackageDecoder.Instance.Decode(ref reader)!.Transactions[0].Hash, Is.EqualTo(tx.Hash));
    }

    [Test]
    public void Rejects_null_transaction_entry()
    {
        Rlp encoded = Rlp.Encode(Rlp.Encode(new[] { Rlp.Encode(new byte[] { 0xc0 }) }),
            Rlp.Encode(new[] { Rlp.Encode(new byte[] { 1 }), Rlp.Encode(Keccak.Zero) }));
        Assert.Throws<RlpException>(() =>
        {
            RlpReader reader = new(encoded.Bytes);
            InclusionListProofPackageDecoder.Instance.Decode(ref reader);
        });
    }

    [TestCase(Eip8288Constants.MaxAggregateProofBytes, true)]
    [TestCase(Eip8288Constants.MaxAggregateProofBytes + 1, false)]
    public void Proof_decoder_matches_native_size_bound(int proofBytes, bool accepted)
    {
        InclusionListProofPackage list = new()
        {
            Transactions = [],
            RecursiveStark = new RecursiveStark(new byte[proofBytes], Keccak.Zero)
        };
        Rlp encoded = InclusionListProofPackageDecoder.Instance.Encode(list);
        if (accepted)
        {
            RlpReader reader = new(encoded.Bytes);
            Assert.That(InclusionListProofPackageDecoder.Instance.Decode(ref reader)!.RecursiveStark.StarkProof, Has.Length.EqualTo(proofBytes));
        }
        else
        {
            Assert.That(() =>
            {
                RlpReader reader = new(encoded.Bytes);
                InclusionListProofPackageDecoder.Instance.Decode(ref reader);
            }, Throws.InstanceOf<RlpException>());
        }
    }

}
