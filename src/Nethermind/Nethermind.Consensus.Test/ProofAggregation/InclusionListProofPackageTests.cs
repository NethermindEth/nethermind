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

    // A direct witness and an aggregate proof with the same bytes over the same single dependency are distinct verdicts.
    [Test]
    public void Verdict_cache_separates_direct_witnesses_from_aggregate_proofs()
    {
        FakeLeanProofVerifier verifier = new(true);
        FrameDependency dependency = new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute("single"), default);
        ValueHash256 covered = Eip8288Dependencies.ComputeDepsHash([dependency]);
        byte[] bytes = [7];
        Assert.That(ProofVerdictCache.VerifyDirect(verifier, dependency, bytes), Is.True);
        Assert.That(ProofVerdictCache.For(verifier).TryGetVerdict(ValueKeccak.Compute(bytes), covered), Is.Null, "no aggregate verdict yet");
        Assert.That(ProofVerdictCache.VerifyRecursive(verifier, in covered, bytes), Is.True);
        Assert.That(verifier.VerificationCalls, Is.EqualTo(2));
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

    private static InclusionListCommitment Commit(byte[] proof, IReadOnlyList<FrameDependency> proofDeps) =>
        new(ValueKeccak.Compute(proof), Eip8288Dependencies.ComputeDepsHash(proofDeps));

    // EIPs#12475: obligations follow from the signed commitment alone. A committed list binds every statically valid entry,
    // whatever its proof; a null commitment binds only dependency-free entries. The builder folds in a committed proof it holds
    // whose proof_deps cover the list's statically valid dependencies, superset included; a malformed entry contributes none.
    [TestCase("malformed")]
    [TestCase("not-held")]
    [TestCase("uncovering")]
    [TestCase("null-commitment")]
    [TestCase("proof-for-empty-dependencies")]
    [TestCase("superset")]
    public void Eligibility_follows_the_commitment_and_builders_fold_held_proofs(string scenario)
    {
        Transaction ordinary = new();
        Transaction covered = ValidDependencyFrame(default);
        Transaction other = ValidDependencyFrame(ValueKeccak.Compute("other"));
        if (scenario == "malformed") other.Frames = [new(FrameMode.DepVerify, FrameFlags.None, null, 0, UInt256.Zero, new byte[1])];
        Transaction[] transactions = scenario == "proof-for-empty-dependencies" ? [ordinary] : [covered, other, ordinary];
        List<FrameDependency> coveredDeps = Eip8288Dependencies.ForTransaction(covered, FrameTxSigHash.ComputeValue);
        List<FrameDependency> proofDeps = scenario switch
        {
            "malformed" or "uncovering" => coveredDeps,
            _ => Eip8288Dependencies.Canonicalize([.. coveredDeps, .. Eip8288Dependencies.ForTransaction(other, FrameTxSigHash.ComputeValue),
                new FrameDependency(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute("surplus"), default)])
        };
        byte[] proof = [1];
        LeanProofStore store = new(FrameTxSigHash.ComputeValue);
        if (scenario != "not-held") store.AddCachedRecursive(proofDeps, proof);
        FakeLeanProofVerifier verifier = new(true);
        CommitteeInclusionList[] lists = [new(0, transactions, scenario == "null-commitment" ? null : Commit(proof, proofDeps))];

        Transaction[] eligible = InclusionListProofValidator.SelectEligible([], lists, Eip8288Prototype.Instance);
        List<RecursiveProofInput> proven = InclusionListProofValidator.ProvenInputs(lists, verifier, Eip8288Prototype.Instance, store,
            out List<ulong> unusable);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(eligible, scenario switch
            {
                "malformed" => Is.EqualTo(new[] { covered, ordinary }),
                "null-commitment" or "proof-for-empty-dependencies" => Is.EqualTo(new[] { ordinary }),
                _ => Is.EqualTo(new[] { covered, other, ordinary })
            });
            Assert.That(proven.Select(p => p.InnerDeps), scenario is "malformed" or "superset"
                ? Is.EqualTo(new[] { proofDeps }) : Is.Empty, "a held proof is folded in whole; the block discards the surplus");
            Assert.That(unusable, Is.EqualTo(scenario is "not-held" or "uncovering" ? new ulong[] { 0 } : []));
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

    // EIP-8288 Test Case 9 for engine_verifyInclusionListProofV1: a commitment to a held wrapper proof whose deps strictly
    // contain the list's is VALID by lookup alone; PROOF_UNKNOWN until a matching il_proof arrives for a node holding neither; an
    // il_proof whose proof_deps do not hash to deps_hash is ignored; known proof_deps omitting a list dependency, or a failing
    // proof, are INVALID. A zero data_hash resolves to the signature hash for every scheme, including leanSTARK.
    [TestCase("held-wrapper", InclusionListProofStatus.Valid, null, 0)]
    [TestCase("il-proof", InclusionListProofStatus.Valid, null, 1)]
    [TestCase("il-proof-fails", InclusionListProofStatus.Invalid, InclusionListProofValidator.InvalidProof, 1)]
    [TestCase("not-held", InclusionListProofStatus.ProofUnknown, InclusionListProofValidator.UnknownProof, 0)]
    [TestCase("verdict-without-bytes", InclusionListProofStatus.ProofUnknown, InclusionListProofValidator.UnknownProof, 0)]
    [TestCase("mismatched-il-proof", InclusionListProofStatus.ProofUnknown, InclusionListProofValidator.UnknownProof, 0)]
    [TestCase("omits-dependency", InclusionListProofStatus.Invalid, InclusionListProofValidator.UncoveredDependency, 0)]
    [TestCase("known-invalid", InclusionListProofStatus.Invalid, InclusionListProofValidator.InvalidProof, 0)]
    [TestCase("no-dependencies", InclusionListProofStatus.Invalid, InclusionListProofValidator.EmptyDependencies, 0)]
    [TestCase("malformed-entry-ignored", InclusionListProofStatus.Valid, null, 0)]
    public void Commitment_verification(string scenario, InclusionListProofStatus expected, string? expectedError, int verifications)
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
        FrameDependency surplus = new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute("surplus"), default);
        List<FrameDependency> proofDeps = scenario == "omits-dependency" ? [surplus] : Eip8288Dependencies.Canonicalize([.. deps, surplus]);
        byte[] proof = [1];
        InclusionListCommitment commitment = Commit(proof, proofDeps);
        FakeLeanProofVerifier verifier = new(scenario is not ("il-proof-fails" or "known-invalid"));
        ValueHash256 depsHash = commitment.DepsHash;
        LeanProofStore store = new(FrameTxSigHash.ComputeValue);
        // A node that validated the mode-1 wrapper holds the proof with its deps; the counts below exclude that verification.
        if (scenario is "held-wrapper" or "omits-dependency" or "malformed-entry-ignored" or "known-invalid" or "no-dependencies"
            or "verdict-without-bytes")
            ProofVerdictCache.VerifyRecursive(verifier, in depsHash, proof, proofDeps);
        if (scenario is "held-wrapper" or "malformed-entry-ignored" or "known-invalid") store.AddVerified(proofDeps, null, proof);
        int before = verifier.VerificationCalls;
        InclusionListProof? ilProof = scenario switch
        {
            "il-proof" or "il-proof-fails" => new(proof, proofDeps),
            "mismatched-il-proof" => new(proof, deps),
            _ => null
        };

        InclusionListProofStatus status = InclusionListProofValidator.VerifyCommitment(transactions, in commitment, ilProof, verifier,
            Eip8288Prototype.Instance, out string? error, store);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(status, Is.EqualTo(expected));
            Assert.That(error, Is.EqualTo(expectedError));
            Assert.That(verifier.VerificationCalls - before, Is.EqualTo(verifications));
            Assert.That(deps.Single().DataHash, Is.EqualTo(FrameTxSigHash.ComputeValue(covered)));
            if (scenario == "il-proof")
            {
                Assert.That(store.TryGetRecursiveProof(commitment.StarkProofHash, depsHash, out byte[]? held, out _), Is.True, "retained for building");
                Assert.That(held, Is.EqualTo(proof));
                Assert.That(InclusionListProofValidator.VerifyCommitment(transactions, in commitment, null, verifier, Eip8288Prototype.Instance,
                    out _, store), Is.EqualTo(InclusionListProofStatus.Valid), "a verified il_proof is then held");
            }
        }
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
