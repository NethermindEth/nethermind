// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.ProofAggregation;

public class InclusionListProofsTests
{
    /// <summary>Timestamp of the target block of slot N + 1; the list slot N starts 12 seconds earlier.</summary>
    private const ulong Target = 1_000_000;

    /// <summary>The consensus layer's view freeze deadline of slot N, in Unix milliseconds.</summary>
    private const ulong ViewFreezeMs = (Target - 3) * 1000;

    /// <summary>The consensus layer's attestation deadline of slot N + 1, in Unix milliseconds.</summary>
    private const ulong AttestationMs = (Target + 4) * 1000;

    /// <summary>A later fork that changes <c>AGGREGATED_VK</c>.</summary>
    private const ulong UpgradeAt = 2_000_000;

    private static readonly ValueHash256 UpgradedVk = ValueKeccak.Compute("next AGGREGATED_VK");

    private sealed class Node
    {
        public Node(bool valid = true)
        {
            Clock = new ManualTimestamper(DateTimeOffset.FromUnixTimeSeconds((long)Target - 6).UtcDateTime);
            Verifier = new FakeLeanProofVerifier(valid);
            Store = new LeanProofStore(FrameTxSigHash.ComputeValue);
            OverridableReleaseSpec upgraded = new(Eip8288Prototype.Instance) { Eip8288AggregatedVk = UpgradedVk };
            CustomSpecProvider specs = new(((ForkActivation)0, Eip8288Prototype.Instance), (new ForkActivation(0, UpgradeAt), upgraded));
            Service = new InclusionListProofService(specs, Verifier, Store, Clock);
        }

        public ManualTimestamper Clock { get; }
        public FakeLeanProofVerifier Verifier { get; }
        public LeanProofStore Store { get; }
        public InclusionListProofService Service { get; }

        public void At(ulong timestamp) => Clock.Set(DateTimeOffset.FromUnixTimeSeconds((long)timestamp).UtcDateTime);

        /// <summary>Holds a proof as a node that fully validated a mode-1 wrapper with these deps.</summary>
        public void HoldWrapper(byte[] proof, List<FrameDependency> proofDeps)
        {
            ValueHash256 depsHash = Eip8288Dependencies.ComputeDepsHash(proofDeps);
            ProofVerdictCache.VerifyRecursive(Verifier, Eip8288Verifier.Pinned, in depsHash, proof, proofDeps);
            Store.AddVerified(proofDeps, null, proof);
        }
    }

    [Test]
    public void Verdict_cache_keys_on_proof_hash_and_deps_hash([Values] bool valid)
    {
        FakeLeanProofVerifier backend = new(valid);
        ValueHash256 deps = ValueKeccak.Compute([1]);
        byte[] proof = [1];
        using (Assert.EnterMultipleScope())
        {
            for (int check = 0; check < 4; check++)
                Assert.That(ProofVerdictCache.VerifyRecursive(backend, Eip8288Verifier.Pinned, in deps, proof), Is.EqualTo(valid));
            Assert.That(backend.VerificationCalls, Is.EqualTo(1));
            proof[0] = 2;
            Assert.That(ProofVerdictCache.VerifyRecursive(backend, Eip8288Verifier.Pinned, in deps, proof), Is.EqualTo(valid));
            Assert.That(backend.VerificationCalls, Is.EqualTo(2));
            deps = ValueKeccak.Compute([2]);
            Assert.That(ProofVerdictCache.VerifyRecursive(backend, Eip8288Verifier.Pinned, in deps, proof), Is.EqualTo(valid));
            Assert.That(backend.VerificationCalls, Is.EqualTo(3));
        }
    }

    // EIP-8288 Test Case 12: verdict caches are separate per verification kind and verifier. A valid direct witness w for d
    // misses the aggregate cache in a one-dependency mode-1 wrapper, a cached aggregate verdict does not satisfy a direct
    // lookup, an aggregate verdict never answers under another AGGREGATED_VK, and a direct verdict survives a key change that
    // leaves its scheme parameters unchanged.
    [Test]
    public void Verdict_caches_are_scoped_to_kind_and_verifier()
    {
        FakeLeanProofVerifier verifier = new(true);
        FrameDependency dependency = new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute("single"), default);
        ValueHash256 covered = Eip8288Dependencies.ComputeDepsHash([dependency]);
        Eip8288Verifier upgraded = new(UpgradedVk);
        byte[] bytes = [7];
        ValueHash256 hash = ValueKeccak.Compute(bytes);
        ProofVerdictCache cache = ProofVerdictCache.For(verifier);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ProofVerdictCache.VerifyDirect(verifier, Eip8288Verifier.Pinned, dependency, bytes), Is.True);
            Assert.That(cache.TryGetVerdict(hash, covered, Eip8288Verifier.PinnedAggregatedVk), Is.Null, "no aggregate verdict from a direct witness");
            Assert.That(ProofVerdictCache.VerifyRecursive(verifier, Eip8288Verifier.Pinned, in covered, bytes), Is.True);
            Assert.That(verifier.VerificationCalls, Is.EqualTo(2));
            Assert.That(cache.TryGetVerdict(hash, covered, UpgradedVk), Is.Null);
            Assert.That(ProofVerdictCache.VerifyRecursive(verifier, upgraded, in covered, bytes), Is.False, "an old proof never verifies under a new key");
            Assert.That(ProofVerdictCache.VerifyDirect(verifier, upgraded, dependency, bytes), Is.True);
            Assert.That(verifier.VerificationCalls, Is.EqualTo(3), "the direct verdict is reused under unchanged scheme parameters");
            Assert.That(cache.TryGetDirectVerdict(dependency, hash, upgraded), Is.True);
        }
    }

    [Test]
    public void Verdict_cache_is_bounded()
    {
        FakeLeanProofVerifier backend = new(true);
        ValueHash256 deps = ValueKeccak.Compute([1]);
        for (int i = 0; i <= ProofVerdictCache.Capacity; i++)
            ProofVerdictCache.VerifyRecursive(backend, Eip8288Verifier.Pinned, in deps, BitConverter.GetBytes(i));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ProofVerdictCache.For(backend).Count, Is.EqualTo(ProofVerdictCache.Capacity));
            // The oldest verdict was evicted, so its proof is verified again.
            ProofVerdictCache.VerifyRecursive(backend, Eip8288Verifier.Pinned, in deps, BitConverter.GetBytes(0));
            Assert.That(backend.VerificationCalls, Is.EqualTo(ProofVerdictCache.Capacity + 2));
        }
    }

    // EIP-8288 Test Case 9 for engine_verifyInclusionListProofV1: a commitment to a held wrapper proof whose deps strictly
    // contain the list's is VALID by lookup alone; PROOF_UNKNOWN for a node holding neither the wrapper nor an il_proof, or only a
    // VALID verdict without the proof bytes, until a matching il_proof is supplied, which then reuses a cached verdict without
    // verifying again; an il_proof whose proof_deps do not hash to deps_hash is ignored; known proof_deps omitting a list
    // dependency, a held INVALID verdict with or without an il_proof, a failing or malformed matching proof or a list without
    // dependencies are INVALID. A transaction whose dependency frame carries no triples is dependency-bearing but contributes no
    // dependencies. Under a timestamp whose fork has another AGGREGATED_VK, a held proof does not count. A zero data_hash
    // resolves to the signature hash for every scheme.
    [TestCase("held-wrapper", InclusionListProofStatus.Valid, null, 0)]
    [TestCase("il-proof", InclusionListProofStatus.Valid, null, 1)]
    [TestCase("il-proof-fails", InclusionListProofStatus.Invalid, InclusionListProofValidator.InvalidProof, 1)]
    [TestCase("not-held", InclusionListProofStatus.ProofUnknown, InclusionListProofValidator.UnknownProof, 0)]
    [TestCase("verdict-without-bytes", InclusionListProofStatus.ProofUnknown, InclusionListProofValidator.UnknownProof, 0)]
    [TestCase("verdict-without-bytes-resupplied", InclusionListProofStatus.Valid, null, 0)]
    [TestCase("mismatched-il-proof", InclusionListProofStatus.ProofUnknown, InclusionListProofValidator.UnknownProof, 0)]
    [TestCase("unsorted-matching-il-proof", InclusionListProofStatus.Invalid, InclusionListProofValidator.NonCanonicalProofDeps, 0)]
    [TestCase("reordered-il-proof", InclusionListProofStatus.ProofUnknown, InclusionListProofValidator.UnknownProof, 0)]
    [TestCase("omits-dependency", InclusionListProofStatus.Invalid, InclusionListProofValidator.UncoveredDependency, 0)]
    [TestCase("known-invalid", InclusionListProofStatus.Invalid, InclusionListProofValidator.InvalidProof, 0)]
    [TestCase("known-invalid-with-il-proof", InclusionListProofStatus.Invalid, InclusionListProofValidator.InvalidProof, 0)]
    [TestCase("no-dependencies", InclusionListProofStatus.Invalid, InclusionListProofValidator.EmptyDependencies, 0)]
    [TestCase("only-dependency-bearing-entry-invalid", InclusionListProofStatus.Invalid, InclusionListProofValidator.EmptyDependencies, 0)]
    [TestCase("only-empty-dependency-frame", InclusionListProofStatus.Invalid, InclusionListProofValidator.EmptyDependencies, 0)]
    [TestCase("malformed-entry-ignored", InclusionListProofStatus.Valid, null, 0)]
    [TestCase("other-fork-held", InclusionListProofStatus.ProofUnknown, InclusionListProofValidator.UnknownProof, 0)]
    [TestCase("other-fork-il-proof", InclusionListProofStatus.Invalid, InclusionListProofValidator.InvalidProof, 1)]
    public void Commitment_verification(string scenario, InclusionListProofStatus expected, string? expectedError, int verifications)
    {
        Node node = new(scenario is not ("il-proof-fails" or "known-invalid" or "known-invalid-with-il-proof"));
        Transaction covered = ValidDependencyFrame(default);
        Transaction malformed = Malformed();
        Transaction[] transactions = scenario switch
        {
            "no-dependencies" => [new Transaction()],
            "only-dependency-bearing-entry-invalid" => [malformed, new Transaction()],
            "only-empty-dependency-frame" => [EmptyDependencyFrame(), new Transaction()],
            "malformed-entry-ignored" => [covered, malformed],
            _ => [covered]
        };
        List<FrameDependency> deps = Eip8288Dependencies.ForTransaction(covered, FrameTxSigHash.ComputeValue);
        FrameDependency surplus = new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute("surplus"), default);
        List<FrameDependency> proofDeps = scenario == "omits-dependency" ? [surplus] : Eip8288Dependencies.Canonicalize([.. deps, surplus]);
        byte[] proof = [1];
        InclusionListCommitment commitment = Commit(proof, proofDeps);
        ValueHash256 depsHash = commitment.DepsHash;
        if (scenario is "held-wrapper" or "malformed-entry-ignored" or "known-invalid" or "known-invalid-with-il-proof" or "other-fork-held"
            or "omits-dependency" or "no-dependencies")
            node.HoldWrapper(proof, proofDeps);
        if (scenario is "verdict-without-bytes" or "verdict-without-bytes-resupplied")
            ProofVerdictCache.VerifyRecursive(node.Verifier, Eip8288Verifier.Pinned, in depsHash, proof, proofDeps);
        int before = node.Verifier.VerificationCalls;
        List<FrameDependency> unsorted = [.. proofDeps.AsEnumerable().Reverse()];
        if (scenario == "unsorted-matching-il-proof") commitment = new(ValueKeccak.Compute(proof), new InclusionListProof(proof, unsorted).DepsHash);
        InclusionListProof? ilProof = scenario switch
        {
            "il-proof" or "il-proof-fails" or "known-invalid-with-il-proof" or "other-fork-il-proof" or "verdict-without-bytes-resupplied"
                => new(proof, proofDeps),
            "mismatched-il-proof" => new(proof, deps),
            "unsorted-matching-il-proof" or "reordered-il-proof" => new(proof, unsorted),
            _ => null
        };
        ulong timestamp = scenario is "other-fork-held" or "other-fork-il-proof" ? UpgradeAt : Target;

        InclusionListProofStatus status = node.Service.Verify(timestamp, transactions, in commitment, ilProof, AttestationMs, out string? error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(status, Is.EqualTo(expected));
            Assert.That(error, Is.EqualTo(expectedError));
            Assert.That(node.Verifier.VerificationCalls - before, Is.EqualTo(verifications));
            Assert.That(deps.Single().DataHash, Is.EqualTo(FrameTxSigHash.ComputeValue(covered)));
            if (expected == InclusionListProofStatus.Valid)
                Assert.That(node.Service.Get(timestamp, commitment.StarkProofHash), Is.Not.Null, "VALID requires and retains the proof bytes");
            if (scenario == "verdict-without-bytes")
                Assert.That(node.Service.Get(timestamp, commitment.StarkProofHash), Is.Null, "get serves bytes only");
            if (scenario is "il-proof" or "verdict-without-bytes-resupplied")
            {
                InclusionListProof? served = node.Service.Get(Target, commitment.StarkProofHash);
                Assert.That(served?.StarkProof, Is.EqualTo(proof), "retained and servable after VALID");
                Assert.That(served?.ProofDeps, Is.EqualTo(proofDeps));
                Assert.That(node.Service.Verify(Target, transactions, in commitment, null, AttestationMs, out _), Is.EqualTo(InclusionListProofStatus.Valid),
                    "a verified il_proof is then held");
            }
        }
    }

    // execution-apis#945: a matching il_proof failing submit's shape checks (size bounds, order, duplicates, enabled schemes) is
    // INVALID without verification and its verdict is recorded, so the commitment stays INVALID without an il_proof. One that
    // does not match, including a reordering of the committed list, is ignored whatever its shape, and records nothing.
    [TestCase("oversized-proof", true, InclusionListProofValidator.OversizedProof)]
    [TestCase("too-many-deps", true, InclusionListProofValidator.TooManyProofDeps)]
    [TestCase("unsorted-deps", true, InclusionListProofValidator.NonCanonicalProofDeps)]
    [TestCase("duplicate-deps", true, InclusionListProofValidator.NonCanonicalProofDeps)]
    [TestCase("disabled-scheme", true, InclusionListProofValidator.DisabledScheme)]
    [TestCase("wide-scheme", true, InclusionListProofValidator.DisabledScheme)]
    [TestCase("oversized-proof", false, null)]
    [TestCase("unsorted-deps", false, null)]
    public void Malformed_il_proof_is_invalid_only_when_it_matches(string shape, bool matches, string? expectedError)
    {
        Node node = new();
        Transaction covered = ValidDependencyFrame(default);
        List<FrameDependency> deps = Eip8288Dependencies.ForTransaction(covered, FrameTxSigHash.ComputeValue);
        FrameDependency other = new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute("other"), default);
        List<FrameDependency> canonical = Eip8288Dependencies.Canonicalize([.. deps, other]);
        byte[] proof = shape == "oversized-proof" ? new byte[Eip8288Constants.MaxAggregateProofBytes + 1] : [6];
        List<FrameDependency> proofDeps = shape switch
        {
            "too-many-deps" => [.. deps, .. Enumerable.Range(0, Eip8288Constants.MaxDepsPerAggregateWrapper).Select(static i =>
                new FrameDependency(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute(BitConverter.GetBytes(i)), default))],
            "unsorted-deps" => [.. canonical.AsEnumerable().Reverse()],
            "duplicate-deps" => [.. canonical, canonical[^1]],
            "disabled-scheme" => [.. canonical, new FrameDependency(0x7f, ValueKeccak.Compute("disabled"), default)],
            _ => canonical
        };
        InclusionListProof ilProof = new(proof, proofDeps);
        if (shape == "wide-scheme")
        {
            byte[] encoded = Eip8288Dependencies.Serialize(proofDeps);
            encoded[30] = 1;
            ilProof = new(proof, proofDeps, Blake2s.Compute(encoded), HasWideScheme: true);
        }
        // Not matching: the committed canonical form of the reordered list, or another proof under the same list.
        InclusionListCommitment commitment = matches ? new(ValueKeccak.Compute(proof), ilProof.DepsHash)
            : shape == "unsorted-deps" ? new(ValueKeccak.Compute(proof), Eip8288Dependencies.ComputeDepsHash(proofDeps))
            : new(ValueKeccak.Compute("another proof"), ilProof.DepsHash);

        InclusionListProofStatus status = node.Service.Verify(Target, [covered], in commitment, ilProof, AttestationMs, out string? error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(status, Is.EqualTo(matches ? InclusionListProofStatus.Invalid : InclusionListProofStatus.ProofUnknown));
            Assert.That(error, Is.EqualTo(expectedError ?? InclusionListProofValidator.UnknownProof));
            Assert.That(node.Verifier.VerificationCalls, Is.Zero, "rejected before verification");
            Assert.That(ProofVerdictCache.For(node.Verifier).TryGetVerdict(commitment.StarkProofHash, commitment.DepsHash, Eip8288Verifier.PinnedAggregatedVk),
                matches ? Is.False : Is.Null, "a matching rejection is recorded; an ignored il_proof records nothing");
            Assert.That(node.Service.Verify(Target, [covered], in commitment, null, AttestationMs, out _),
                Is.EqualTo(matches ? InclusionListProofStatus.Invalid : InclusionListProofStatus.ProofUnknown));
        }
    }

    // engine_getInclusionListProofV1 returns [stark_proof, deps] for a held wrapper's proof hash and null for an unknown hash or
    // under a fork whose AGGREGATED_VK the proof was not verified under; a proof answered VALID stays servable after the
    // mempool drops the wrapper it came from, until the attestation deadline the consensus layer supplied.
    [Test]
    public void Get_serves_held_proofs_under_the_target_fork_only()
    {
        Node node = new();
        Transaction covered = ValidDependencyFrame(default);
        List<FrameDependency> proofDeps = Eip8288Dependencies.ForTransaction(covered, FrameTxSigHash.ComputeValue);
        byte[] proof = [3];
        node.HoldWrapper(proof, proofDeps);
        InclusionListCommitment commitment = Commit(proof, proofDeps);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(node.Service.Get(Target, commitment.StarkProofHash)?.ProofDeps, Is.EqualTo(proofDeps));
            Assert.That(node.Service.Get(Target, ValueKeccak.Compute("unknown")), Is.Null);
            Assert.That(node.Service.Get(UpgradeAt, commitment.StarkProofHash), Is.Null);
            Assert.That(node.Service.Verify(Target, [covered], in commitment, null, AttestationMs, out _), Is.EqualTo(InclusionListProofStatus.Valid));
            node.Store.RetainDirect(static (_, _) => true);
            Assert.That(node.Service.Get(Target, commitment.StarkProofHash)?.StarkProof, Is.EqualTo(proof), "served until the attestation deadline");
            node.At(Target + 4);
            Assert.That(node.Service.Get(Target, commitment.StarkProofHash), Is.Null, "released at the attestation deadline");
        }
    }

    // engine_submitInclusionListProofV1 rejects proofs over the size bounds or with noncanonical or disabled-scheme proof_deps
    // without verifying, and records every verdict, those rejections included. A valid proof's bytes are kept until the view freeze deadline, so a later list
    // committing to it is VALID by cache lookup; once they are released the commitment is PROOF_UNKNOWN and get returns null,
    // until a resupplied il_proof reuses the verdict without verifying again. A failed proof's INVALID verdict answers a later
    // list committing to it, with or without an il_proof. A deadline already past retains nothing.
    [TestCase("valid", InclusionListProofStatus.Valid, null, 1)]
    [TestCase("fails", InclusionListProofStatus.Invalid, InclusionListProofValidator.InvalidProof, 1)]
    [TestCase("empty-proof", InclusionListProofStatus.Invalid, InclusionListProofValidator.EmptyProof, 0)]
    [TestCase("oversized-proof", InclusionListProofStatus.Invalid, InclusionListProofValidator.OversizedProof, 0)]
    [TestCase("empty-deps", InclusionListProofStatus.Invalid, InclusionListProofValidator.EmptyProofDeps, 0)]
    [TestCase("unsorted-deps", InclusionListProofStatus.Invalid, InclusionListProofValidator.NonCanonicalProofDeps, 0)]
    [TestCase("duplicate-deps", InclusionListProofStatus.Invalid, InclusionListProofValidator.NonCanonicalProofDeps, 0)]
    [TestCase("disabled-scheme", InclusionListProofStatus.Invalid, InclusionListProofValidator.DisabledScheme, 0)]
    [TestCase("other-fork", InclusionListProofStatus.Invalid, InclusionListProofValidator.InvalidProof, 1)]
    [TestCase("past-deadline", InclusionListProofStatus.Valid, null, 1)]
    public void Submit(string scenario, InclusionListProofStatus expected, string? expectedError, int verifications)
    {
        Node node = new(scenario != "fails");
        Transaction covered = ValidDependencyFrame(default);
        List<FrameDependency> deps = Eip8288Dependencies.ForTransaction(covered, FrameTxSigHash.ComputeValue);
        FrameDependency other = new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute("other"), default);
        List<FrameDependency> proofDeps = scenario switch
        {
            "empty-deps" => [],
            "unsorted-deps" => [.. Eip8288Dependencies.Canonicalize([.. deps, other]).AsEnumerable().Reverse()],
            "duplicate-deps" => [.. deps, .. deps],
            "disabled-scheme" => [new FrameDependency(0x7f, default, default)],
            _ => Eip8288Dependencies.Canonicalize([.. deps, other])
        };
        byte[] proof = scenario switch
        {
            "empty-proof" => [],
            "oversized-proof" => new byte[Eip8288Constants.MaxAggregateProofBytes + 1],
            _ => [4]
        };

        ulong deadline = scenario == "past-deadline" ? (Target - 7) * 1000 : ViewFreezeMs;

        InclusionListProofStatus status = node.Service.Submit(scenario == "other-fork" ? UpgradeAt : Target, new(proof, proofDeps), deadline,
            out string? error);

        InclusionListCommitment commitment = Commit(proof, proofDeps);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(status, Is.EqualTo(expected));
            Assert.That(error, Is.EqualTo(expectedError));
            Assert.That(node.Verifier.VerificationCalls, Is.EqualTo(verifications));
            Assert.That(node.Service.Get(Target, commitment.StarkProofHash) is not null, Is.EqualTo(scenario == "valid"), "retains only a valid proof");
            ValueHash256 key = new InclusionListProof(proof, proofDeps).DepsHash;
            Assert.That(ProofVerdictCache.For(node.Verifier).TryGetVerdict(commitment.StarkProofHash, key, Eip8288Verifier.For(
                scenario == "other-fork" ? new OverridableReleaseSpec(Eip8288Prototype.Instance) { Eip8288AggregatedVk = UpgradedVk } : Eip8288Prototype.Instance).AggregatedVk),
                Is.EqualTo(expected == InclusionListProofStatus.Valid), "every verdict is recorded, shape rejections included");
            if (scenario == "valid")
            {
                Assert.That(node.Service.Verify(Target, [covered], in commitment, null, AttestationMs, out _), Is.EqualTo(InclusionListProofStatus.Valid));
                Assert.That(node.Verifier.VerificationCalls, Is.EqualTo(1), "a later list check is a cache lookup");
                Assert.That(node.Service.Get(Target, commitment.StarkProofHash), Is.Not.Null, "kept for the list until the attestation deadline");
            }
            if (scenario is "valid" or "past-deadline")
            {
                // Bytes released: the view freeze deadline passed and no list answered VALID for the proof.
                node.At(scenario == "valid" ? Target + 4 : Target);
                Assert.That(node.Service.Get(Target, commitment.StarkProofHash), Is.Null, "get serves bytes only");
                Assert.That(node.Service.Verify(Target, [covered], in commitment, null, AttestationMs, out string? unknown),
                    Is.EqualTo(InclusionListProofStatus.ProofUnknown), "a VALID verdict without the bytes is not a held proof");
                Assert.That(unknown, Is.EqualTo(InclusionListProofValidator.UnknownProof));
                Assert.That(node.Service.Verify(Target, [covered], in commitment, new(proof, proofDeps), (Target + 16) * 1000, out _),
                    Is.EqualTo(InclusionListProofStatus.Valid), "a resupplied il_proof reuses the verdict");
                Assert.That(node.Verifier.VerificationCalls, Is.EqualTo(1), "without verifying again");
                Assert.That(node.Service.Get(Target, commitment.StarkProofHash)?.StarkProof, Is.EqualTo(proof), "and is served again");
            }
            if (scenario == "fails")
            {
                Assert.That(node.Service.Verify(Target, [covered], in commitment, null, AttestationMs, out string? withoutProof),
                    Is.EqualTo(InclusionListProofStatus.Invalid), "a cached INVALID verdict answers without an il_proof");
                Assert.That(withoutProof, Is.EqualTo(InclusionListProofValidator.InvalidProof));
                Assert.That(node.Service.Verify(Target, [covered], in commitment, new(proof, proofDeps), AttestationMs, out _),
                    Is.EqualTo(InclusionListProofStatus.Invalid), "and with one");
                Assert.That(node.Verifier.VerificationCalls, Is.EqualTo(1), "without verifying again");
            }
        }
    }

    // Proof Availability retention, until deadlines the consensus layer supplies: a topic proof is kept until the view freeze
    // deadline it was submitted with, and a proof answered VALID for a list until that call's attestation deadline; a later
    // deadline keeps the proof after an earlier one passes, and a deadline already past adds no retention. Slot length plays no
    // part, so deadlines away from t=4s and t=9s of a 12-second slot are honored as given.
    [TestCase("topic-only", Target - 3, false)]
    [TestCase("topic-only", Target - 4, true)]
    [TestCase("valid-for-list", Target + 3, true)]
    [TestCase("valid-for-list", Target + 4, false)]
    [TestCase("later-slot", Target + 4, true)]
    [TestCase("later-slot", Target + 12 + 4, false)]
    [TestCase("past-attestation-deadline", Target - 4, true)]
    [TestCase("past-attestation-deadline", Target - 3, false)]
    [TestCase("short-slots", Target + 1, true)]
    [TestCase("short-slots", Target + 2, false)]
    public void Retention_follows_each_supplied_deadline(string scenario, ulong now, bool retained)
    {
        Node node = new();
        Transaction covered = ValidDependencyFrame(default);
        List<FrameDependency> proofDeps = Eip8288Dependencies.ForTransaction(covered, FrameTxSigHash.ComputeValue);
        byte[] proof = [5];
        InclusionListCommitment commitment = Commit(proof, proofDeps);
        Assert.That(node.Service.Submit(Target, new(proof, proofDeps), ViewFreezeMs, out _), Is.EqualTo(InclusionListProofStatus.Valid));
        ulong? attestationDeadline = scenario switch
        {
            "valid-for-list" or "later-slot" => AttestationMs,
            "past-attestation-deadline" => (Target - 7) * 1000,
            "short-slots" => (Target + 2) * 1000,
            _ => null
        };
        if (attestationDeadline is { } deadline)
            Assert.That(node.Service.Verify(Target, [covered], in commitment, null, deadline, out _), Is.EqualTo(InclusionListProofStatus.Valid));
        if (scenario == "later-slot")
            Assert.That(node.Service.Verify(Target + 12, [covered], in commitment, null, (Target + 12 + 4) * 1000, out _),
                Is.EqualTo(InclusionListProofStatus.Valid));

        node.At(now);

        Assert.That(node.Service.Get(Target, commitment.StarkProofHash) is not null, Is.EqualTo(retained));
    }

    // A consensus-layer deadline of ulong.MaxValue must still be evictable at the bound, or retention would spin under its lock.
    [Test]
    public void Retention_bound_evicts_proofs_with_maximal_deadlines()
    {
        Node node = new();
        FrameDependency dependency = new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute("d"), default);
        for (int i = 0; i <= InclusionListProofRetention.MaxProofs; i++)
            Assert.That(node.Service.Submit(Target, new(BitConverter.GetBytes(i), [dependency]), ulong.MaxValue, out _), Is.EqualTo(InclusionListProofStatus.Valid));
        Assert.That(node.Service.Retention.Count, Is.EqualTo(InclusionListProofRetention.MaxProofs));
    }

    [Test]
    public void Retention_is_bounded_by_the_peak_of_three_committees()
    {
        Node node = new();
        FrameDependency dependency = new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute("d"), default);
        for (int i = 0; i <= InclusionListProofRetention.MaxProofs; i++)
            node.Service.Submit(Target + (ulong)(i % 3) * 12, new(BitConverter.GetBytes(i), [dependency]), ViewFreezeMs + (ulong)(i % 3) * 12_000, out _);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(node.Service.Retention.Count, Is.EqualTo(InclusionListProofRetention.MaxProofs));
            Assert.That(InclusionListProofRetention.MaxBytes, Is.EqualTo(130L * 1024 * 1024), "80 il_proofs of 1.625 MiB");
        }
    }

    // EIP-8288 Inclusion Enforcement and Test Case 9: a signed list always obligates its dependency-free transactions, and its
    // dependency-bearing ones only when this node holds a valid proof covering them, superset included. An unknown or
    // non-covering proof, a VALID verdict without the proof bytes, a null commitment and a commitment on a list without
    // dependencies are treated as null; an entry whose dependency frame fails static validity or carries no triples is not
    // dependency-free, so it is never obligated. The builder folds a held committed proof in whole and discards the surplus.
    [TestCase("superset")]
    [TestCase("not-held")]
    [TestCase("verdict-without-bytes")]
    [TestCase("uncovering")]
    [TestCase("null-commitment")]
    [TestCase("empty-dependencies")]
    [TestCase("malformed")]
    [TestCase("empty-dependency-frame")]
    [TestCase("other-fork")]
    public void Obligations_follow_held_proofs(string scenario)
    {
        Node node = new();
        Transaction ordinary = new();
        Transaction covered = ValidDependencyFrame(default);
        Transaction other = scenario switch
        {
            "malformed" => Malformed(),
            "empty-dependency-frame" => EmptyDependencyFrame(),
            _ => ValidDependencyFrame(ValueKeccak.Compute("other"))
        };
        Transaction[] transactions = scenario == "empty-dependencies" ? [Malformed(), ordinary] : [covered, other, ordinary];
        List<FrameDependency> coveredDeps = Eip8288Dependencies.ForTransaction(covered, FrameTxSigHash.ComputeValue);
        FrameDependency surplus = new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute("surplus"), default);
        List<FrameDependency> proofDeps = scenario switch
        {
            "uncovering" => coveredDeps,
            "malformed" or "empty-dependency-frame" or "empty-dependencies" => Eip8288Dependencies.Canonicalize([.. coveredDeps, surplus]),
            _ => Eip8288Dependencies.Canonicalize([.. coveredDeps, .. Eip8288Dependencies.ForTransaction(other, FrameTxSigHash.ComputeValue), surplus])
        };
        byte[] proof = [1];
        if (scenario == "verdict-without-bytes")
        {
            ValueHash256 depsHash = Eip8288Dependencies.ComputeDepsHash(proofDeps);
            ProofVerdictCache.VerifyRecursive(node.Verifier, Eip8288Verifier.Pinned, in depsHash, proof, proofDeps);
        }
        else if (scenario != "not-held") node.HoldWrapper(proof, proofDeps);
        CommitteeInclusionList[] lists = [new(0, transactions, scenario == "null-commitment" ? null : Commit(proof, proofDeps))];
        IReleaseSpec target = scenario == "other-fork" ? new OverridableReleaseSpec(Eip8288Prototype.Instance) { Eip8288AggregatedVk = UpgradedVk }
            : Eip8288Prototype.Instance;

        node.Service.ResolveObligations(lists, target);
        Transaction[] eligible = InclusionListProofValidator.SelectEligible([], lists, target);
        List<RecursiveProofInput> proven = node.Service.ProvenInputs(lists, target);

        bool obligates = scenario is "superset" or "malformed" or "empty-dependency-frame";
        using (Assert.EnterMultipleScope())
        {
            Assert.That(lists[0].HoldsValidProof, Is.EqualTo(obligates));
            Assert.That(eligible, obligates
                ? Is.EqualTo(scenario == "superset" ? new[] { covered, other, ordinary } : new[] { covered, ordinary })
                : Is.EqualTo(new[] { ordinary }));
            Assert.That(proven.Select(p => p.InnerDeps), obligates ? Is.EqualTo(new[] { proofDeps }) : Is.Empty,
                "a held proof is folded in whole; the block discards the surplus");
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

    private static InclusionListCommitment Commit(byte[] proof, IReadOnlyList<FrameDependency> proofDeps) =>
        new(ValueKeccak.Compute(proof), Eip8288Dependencies.ComputeDepsHash(proofDeps));

    private static Transaction Malformed()
    {
        Transaction transaction = ValidDependencyFrame(ValueKeccak.Compute("malformed"));
        transaction.Frames = [new(FrameMode.DepVerify, FrameFlags.None, null, 0, UInt256.Zero, new byte[1])];
        return transaction;
    }

    /// <summary>A transaction whose only dependency verification frame carries no triples: dependency-bearing, without
    /// dependencies, and statically invalid.</summary>
    private static Transaction EmptyDependencyFrame()
    {
        Transaction transaction = ValidDependencyFrame(ValueKeccak.Compute("empty"));
        transaction.Frames = [new(FrameMode.DepVerify, FrameFlags.None, null, 0, UInt256.Zero, Array.Empty<byte>())];
        return transaction;
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
}
