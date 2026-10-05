// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System.Collections.Generic;
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
        List<FrameDependency> dependencies = Eip8288Dependencies.ForTransaction(transaction);
        RecursiveStark proof = new([1], new Hash256(Eip8288Dependencies.ComputeDepsHash(dependencies)));
        byte[] metadata = Eip8288Dependencies.Serialize(dependencies);
        Assert.That(InclusionListProofValidator.Validate([transaction], proof, verifier, out _, out _, provenDependencies: metadata), Is.True);
        Assert.That(InclusionListProofValidator.Validate([transaction], proof, verifier, out _, out _, provenDependencies: metadata), Is.True);
        Assert.That(verifier.VerificationCalls, Is.EqualTo(1));
        proof.StarkProof[0] = 2;
        Assert.That(InclusionListProofValidator.Validate([transaction], proof, verifier, out _, out _, provenDependencies: metadata), Is.True);
        Assert.That(verifier.VerificationCalls, Is.EqualTo(2));
    }

    [Test]
    public void Shared_cache_consults_the_request_memo_for_positive_and_negative_verdicts([Values] bool valid)
    {
        FakeLeanProofVerifier backend = new(valid);
        InclusionListProofVerifier verifier = new(backend);
        Transaction transaction = DepTx(Eip8288Constants.LeanSphincsScheme);
        List<FrameDependency> dependencies = Eip8288Dependencies.ForTransaction(transaction);
        byte[] metadata = Eip8288Dependencies.Serialize(dependencies);
        RecursiveStark proof = new([1], new Hash256(Eip8288Dependencies.ComputeDepsHash(dependencies)));
        for (int check = 0; check < 4; check++)
            Assert.That(InclusionListProofValidator.Validate([transaction], proof, verifier, out _, out _, provenDependencies: metadata), Is.EqualTo(valid));
        Assert.That(backend.VerificationCalls, Is.EqualTo(1));
        proof.StarkProof[0] = 2;
        Assert.That(InclusionListProofValidator.Validate([transaction], proof, verifier, out _, out _, provenDependencies: metadata), Is.EqualTo(valid));
        Assert.That(backend.VerificationCalls, Is.EqualTo(2));
    }

    [Test]
    public void Request_memo_reuses_only_identical_proof_and_public_inputs()
    {
        FakeLeanProofVerifier backend = new(true);
        InclusionListProofVerifier verifier = new(backend);
        ValueHash256 deps = ValueKeccak.Compute([1]);
        byte[] key = [3];
        byte[] proof = [1];
        for (int check = 0; check < 4; check++) Assert.That(verifier.VerifyRecursiveStark(in deps, key, proof), Is.True);
        Assert.That(backend.VerificationCalls, Is.EqualTo(1));
        proof[0] = 2;
        Assert.That(verifier.VerifyRecursiveStark(in deps, key, proof), Is.True);
        Assert.That(backend.VerificationCalls, Is.EqualTo(2));
        key[0] = 4;
        Assert.That(verifier.VerifyRecursiveStark(in deps, key, proof), Is.True);
        Assert.That(backend.VerificationCalls, Is.EqualTo(3));
        deps = ValueKeccak.Compute([2]);
        Assert.That(verifier.VerifyRecursiveStark(in deps, key, proof), Is.True);
        Assert.That(backend.VerificationCalls, Is.EqualTo(4));
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

    [TestCase("malformed")]
    [TestCase("uncovered")]
    [TestCase("invalid-proof")]
    [TestCase("missing-proof")]
    [TestCase("missing-metadata")]
    public void Eligibility_preserves_independent_entries_when_one_frame_or_package_is_bad(string scenario)
    {
        Transaction ordinary = new();
        Transaction covered = ValidDependencyFrame(default);
        Transaction bad = ValidDependencyFrame(ValueKeccak.Compute("uncovered"));
        if (scenario == "malformed") bad.Frames = [new(FrameMode.DepVerify, FrameFlags.None, null, 0, UInt256.Zero, new byte[1])];
        byte[] metadata = Eip8288Dependencies.Serialize(Eip8288Dependencies.ForTransaction(covered));
        RecursiveStark? proof = scenario == "missing-proof" ? null : new([1], new Hash256(ValueKeccak.Compute(metadata)));
        FakeLeanProofVerifier verifier = new(scenario != "invalid-proof");

        Transaction[] eligible = InclusionListProofValidator.SelectEligible([covered, bad, ordinary], proof,
            scenario == "missing-metadata" ? null : metadata, verifier, Eip8288Prototype.Instance, out _, out _);

        Assert.That(eligible, scenario is "malformed" or "uncovered" ? Is.EqualTo(new[] { covered, ordinary }) : Is.EqualTo(new[] { ordinary }));
        Assert.That(verifier.VerificationCalls, Is.EqualTo(scenario is "missing-proof" or "missing-metadata" ? 0 : 1));
    }

    [TestCase("duplicate")]
    [TestCase("padding")]
    [TestCase("unknown-scheme")]
    [TestCase("oversized")]
    public void Noncanonical_or_oversized_membership_metadata_is_rejected_before_native(string scenario)
    {
        Transaction covered = ValidDependencyFrame(default);
        byte[] metadata = Eip8288Dependencies.Serialize(Eip8288Dependencies.ForTransaction(covered));
        metadata = scenario switch
        {
            "duplicate" => [.. metadata, .. metadata],
            "oversized" => new byte[Eip8288Constants.MaxInclusionListDependencyBytes + Eip8288Constants.DependencyTripleLength],
            _ => metadata
        };
        if (scenario == "padding") metadata[0] = 1;
        if (scenario == "unknown-scheme") metadata[31] = 1;
        FakeLeanProofVerifier verifier = new(true);
        RecursiveStark proof = new([1], new Hash256(ValueKeccak.Compute(metadata)));
        Assert.That(InclusionListProofValidator.SelectEligible([covered], proof, metadata, verifier,
            Eip8288Prototype.Instance, out _, out _), Is.Empty);
        Assert.That(verifier.VerificationCalls, Is.Zero);
    }

    private static Transaction ValidDependencyFrame(ValueHash256 message)
    {
        FrameDependency dep = new(Eip8288Constants.LeanSphincsScheme, message, default);
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
        foreach (Transaction tx in txs) deps.AddRange(Eip8288Dependencies.ForTransaction(tx));

        InclusionListProofPackage focil = new()
        {
            Transactions = txs,
            RecursiveStark = new RecursiveStark([1], new Hash256(Eip8288Dependencies.ComputeDepsHash(deps))),
            ProvenDependencies = Eip8288Dependencies.Serialize(Eip8288Dependencies.Canonicalize(deps)),
        };

        Assert.That(InclusionListProofValidator.Validate(focil, Accepting, out string? error), Is.True, error);
    }

    [Test]
    public void Rejects_deps_hash_mismatch()
    {
        InclusionListProofPackage focil = new()
        {
            Transactions = [DepTx(Eip8288Constants.LeanSphincsScheme)],
            RecursiveStark = new RecursiveStark([1], Keccak.Compute("wrong")),
            ProvenDependencies = Eip8288Dependencies.Serialize(Eip8288Dependencies.ForTransaction(DepTx(Eip8288Constants.LeanSphincsScheme))),
        };

        Assert.That(InclusionListProofValidator.Validate(focil, Accepting, out string? error), Is.False);
        Assert.That(error, Is.EqualTo(InclusionListProofValidator.DepsHashMismatch));
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

    [TestCase(Eip8288Constants.MaxProofBytes, true)]
    [TestCase(Eip8288Constants.MaxProofBytes + 1, false)]
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
