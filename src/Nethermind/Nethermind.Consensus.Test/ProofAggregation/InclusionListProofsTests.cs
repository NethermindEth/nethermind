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
    private static readonly ValueHash256 UpgradedVk = ValueKeccak.Compute("next AGGREGATED_VK");

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

    // Local verdict caches are separate per verification kind and verifier. A valid direct witness w for d
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

    // EIP-8288 Attesters Validity Verification and Test Case 9: each entry binds independently. A dependency-free entry always
    // binds; a dependency-bearing one binds when the list's nonempty proof verifies under the target fork's AGGREGATED_VK and its
    // proof_deps contain dependencies(tx), superset included. An empty, failing, noncanonical or unknown-scheme proof imposes no
    // dependency-bearing obligation, and one uncovered or malformed entry does not disable a covered one. An entry whose dependency
    // frame fails static validity or carries no triples is not dependency-free, so it never binds. The builder folds a verified
    // proof in whole and the block aggregate discards the surplus.
    [TestCase("superset")]
    [TestCase("partial")]
    [TestCase("empty-proof")]
    [TestCase("empty-proof-deps")]
    [TestCase("invalid-proof")]
    [TestCase("unsorted")]
    [TestCase("duplicate")]
    [TestCase("unknown-scheme")]
    [TestCase("wide-scheme")]
    [TestCase("malformed")]
    [TestCase("empty-dependency-frame")]
    [TestCase("other-fork")]
    public void Obligations_follow_the_inline_proof(string scenario)
    {
        FakeLeanProofVerifier verifier = new(scenario != "invalid-proof");
        InclusionListProofService service = new(verifier);
        Transaction ordinary = new();
        Transaction covered = ValidDependencyFrame(default, Eip8288Constants.LeanStarkScheme);
        Transaction other = scenario switch
        {
            "malformed" => Malformed(),
            "empty-dependency-frame" => EmptyDependencyFrame(),
            _ => ValidDependencyFrame(ValueKeccak.Compute("other"))
        };
        List<FrameDependency> coveredDeps = Eip8288Dependencies.ForTransaction(covered, FrameTxSigHash.ComputeValue);
        FrameDependency surplus = new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute("surplus"), default);
        List<FrameDependency> proofDeps = scenario switch
        {
            "partial" or "malformed" or "empty-dependency-frame" => Eip8288Dependencies.Canonicalize([.. coveredDeps, surplus]),
            _ => Eip8288Dependencies.Canonicalize([.. coveredDeps, .. Eip8288Dependencies.ForTransaction(other, FrameTxSigHash.ComputeValue), surplus])
        };
        byte[][] encoded = Encode(proofDeps);
        switch (scenario)
        {
            case "unsorted": encoded = [.. encoded.Reverse()]; break;
            case "duplicate": encoded = [encoded[0], .. encoded]; break;
            case "unknown-scheme": encoded[^1][31] = 0x7f; break;
            case "wide-scheme": encoded[^1][30] = 1; break;
            case "empty-proof-deps": encoded = []; break;
        }
        byte[] proof = scenario == "empty-proof" ? [] : [1];
        CommitteeInclusionList[] lists = [new([covered, other, ordinary], proof, encoded)];
        IReleaseSpec target = scenario == "other-fork" ? new OverridableReleaseSpec(Eip8288Prototype.Instance) { Eip8288AggregatedVk = UpgradedVk }
            : Eip8288Prototype.Instance;

        service.ResolveObligations(lists, target);
        Transaction[] eligible = InclusionListProofValidator.SelectEligible([], lists, target);
        List<RecursiveProofInput> proven = InclusionListProofService.ProvenInputs(lists);

        bool verified = scenario is "superset" or "partial" or "malformed" or "empty-dependency-frame";
        using (Assert.EnterMultipleScope())
        {
            Assert.That(lists[0].VerifiedProofDeps, verified ? Is.EquivalentTo(proofDeps) : Is.Null);
            Assert.That(eligible, verified
                ? Is.EqualTo(scenario == "superset" ? new[] { covered, other, ordinary } : new[] { covered, ordinary })
                : Is.EqualTo(new[] { ordinary }));
            Assert.That(proven.Select(p => p.InnerDeps), verified ? Is.EqualTo(new[] { proofDeps }) : Is.Empty,
                "a verified proof is folded in whole; the block discards the surplus");
            Assert.That(verifier.VerificationCalls, Is.EqualTo(scenario is "empty-proof" or "empty-proof-deps" or "unsorted" or "duplicate"
                or "unknown-scheme" or "wide-scheme" ? 0 : 1), "a malformed proof is rejected without verification");
        }
    }

    // EIP-8288 Test Case 9: the same signed list has the same proof verdict under the target block's fork, so resolving it again,
    // as forkchoiceUpdated, newPayload and a later compliance check do, reuses the cached verdict.
    // Wrapper traffic evicting the shared verdict cache must not make each forkchoiceUpdated verify the lists natively again.
    [Test]
    public void Resolving_the_same_list_again_reuses_its_verdict([Values] bool sharedCacheEvicted)
    {
        FakeLeanProofVerifier verifier = new(true);
        InclusionListProofService service = new(verifier);
        Transaction covered = ValidDependencyFrame(default);
        byte[][] proofDeps = Encode(Eip8288Dependencies.ForTransaction(covered, FrameTxSigHash.ComputeValue));
        int otherVerifications = 0;
        for (int i = 0; i < 3; i++)
        {
            CommitteeInclusionList[] lists = [new([covered], [2], proofDeps), new([covered], [2], proofDeps)];
            service.ResolveObligations(lists, Eip8288Prototype.Instance);
            Assert.That(lists.Select(static list => list.VerifiedProofDeps), Has.All.Not.Null);
            Assert.That(InclusionListProofService.ProvenInputs(lists), Has.Count.EqualTo(1), "one proof shared by two lists is folded once");
            if (!sharedCacheEvicted) continue;
            ValueHash256 deps = ValueKeccak.Compute([(byte)i]);
            for (int j = 0; j <= ProofVerdictCache.Capacity; j++, otherVerifications++)
                ProofVerdictCache.VerifyRecursive(verifier, Eip8288Verifier.Pinned, in deps, BitConverter.GetBytes(j));
        }
        Assert.That(verifier.VerificationCalls - otherVerifications, Is.EqualTo(1));
    }

    // A proof the pinned guest cannot have produced, over more dependencies or more leanSTARK dependencies than one of its lists
    // holds, is refused locally without verification: the list then obligates no dependency-bearing entry.
    [TestCase(Eip8288Constants.MaxProofDependencies + 1, 0, false)]
    [TestCase(Eip8288Constants.MaxGenericStarkProofs + 1, Eip8288Constants.MaxGenericStarkProofs + 1, false)]
    [TestCase(Eip8288Constants.MaxGenericStarkProofs + 8, Eip8288Constants.MaxGenericStarkProofs, true)]
    public void A_list_proof_beyond_the_proving_bound_obligates_nothing_and_is_not_verified(int count, int leanStark, bool withinBound)
    {
        FakeLeanProofVerifier verifier = new(true);
        FrameDependency[] dependencies = [.. Enumerable.Range(0, count).Select(i => i < leanStark
            ? new FrameDependency(Eip8288Constants.LeanStarkScheme, ValueKeccak.Compute($"wide-{i}"), default)
            : Dependency($"wide-{i}"))];
        CommitteeInclusionList[] lists = [new([ValidDependencyFrame(default)], [1], Encode(Eip8288Dependencies.Canonicalize(dependencies)))];
        new InclusionListProofService(verifier).ResolveObligations(lists, Eip8288Prototype.Instance);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(lists[0].VerifiedProofDeps, withinBound ? Is.Not.Null : Is.Null);
            Assert.That(verifier.VerificationCalls, Is.EqualTo(withinBound ? 1 : 0));
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

    // EIPs#12475: a locally built list may reuse a held verified proof whole, surplus dependencies included, as its canonical
    // InclusionListProof, so a dependency-bearing entry it covers binds.
    [Test]
    public void Held_proof_choice_reuses_a_verified_superset_proof()
    {
        FakeLeanProofVerifier verifier = new(true);
        LeanProofStore store = new(FrameTxSigHash.ComputeValue);
        FrameDependency covered = Dependency("covered");
        FrameDependency surplus = Dependency("surplus");
        List<FrameDependency> held = Eip8288Dependencies.Canonicalize([surplus, covered]);
        store.AddCachedRecursive(held, [9]);

        HeldInclusionListProof? chosen = new InclusionListProofService(verifier, store).ChooseHeldProof([[covered]], Eip8288Prototype.Instance);

        Assert.That(chosen, Is.Not.Null);
        CommitteeInclusionList list = new([], chosen!.StarkProof, chosen.ProofDeps);
        new InclusionListProofService(verifier).ResolveObligations([list], Eip8288Prototype.Instance);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chosen.StarkProof, Is.EqualTo(new byte[] { 9 }));
            Assert.That(chosen.ProofDeps, Is.EqualTo(Encode(held)), "canonical proof_deps, surplus kept");
            Assert.That(chosen.Covers([covered]), Is.True);
            Assert.That(chosen.Covers([Dependency("other")]), Is.False);
            Assert.That(list.VerifiedProofDeps, Is.EquivalentTo(held), "the attached proof verifies as a signed list's would");
        }
    }

    // Without a held proof covering a candidate that verifies under the target fork's AGGREGATED_VK, a local list carries none.
    [TestCase("no-store")]
    [TestCase("uncovered")]
    [TestCase("partly-covered")]
    [TestCase("invalid-proof")]
    [TestCase("other-fork")]
    [TestCase("without-eip8288")]
    [TestCase("too-many-leanstark")]
    public void Held_proof_choice_needs_a_covering_proof_that_verifies(string scenario)
    {
        FakeLeanProofVerifier verifier = new(scenario != "invalid-proof");
        LeanProofStore store = new(FrameTxSigHash.ComputeValue);
        FrameDependency wanted = Dependency("wanted");
        FrameDependency second = Dependency("second");
        if (scenario == "too-many-leanstark")
            // Over the pinned guest's leanSTARK bound for one list, so it is never attached, as no node could verify it.
            store.AddCachedRecursive(Eip8288Dependencies.Canonicalize([wanted, .. Enumerable.Range(0, Eip8288Constants.MaxGenericStarkProofs + 1)
                .Select(static i => new FrameDependency(Eip8288Constants.LeanStarkScheme, ValueKeccak.Compute($"stark:{i}"), default))]), [9]);
        else
            store.AddCachedRecursive([scenario is "uncovered" or "partly-covered" ? Dependency("held") : wanted], [9]);
        if (scenario == "partly-covered") store.AddCachedRecursive([wanted], [8]);
        IReleaseSpec target = scenario switch
        {
            "other-fork" => new OverridableReleaseSpec(Eip8288Prototype.Instance) { Eip8288AggregatedVk = UpgradedVk },
            "without-eip8288" => new OverridableReleaseSpec(Eip8288Prototype.Instance) { IsEip8288Enabled = false },
            _ => Eip8288Prototype.Instance
        };
        IReadOnlyList<FrameDependency>[] candidates = scenario == "partly-covered" ? [[wanted, second]] : [[wanted]];

        HeldInclusionListProof? chosen = new InclusionListProofService(verifier, scenario == "no-store" ? null : store)
            .ChooseHeldProof(candidates, target);

        Assert.That(chosen, Is.Null);
    }

    // The choice follows the caller's candidate order, not how much a proof covers, and takes the smallest proof covering the
    // first candidate that has one, so neither a large proof nor many unrelated held proofs steer which transactions are listed.
    [Test]
    public void Held_proof_choice_follows_candidate_order_and_takes_the_smallest_covering_proof()
    {
        FakeLeanProofVerifier verifier = new(true);
        LeanProofStore store = new(FrameTxSigHash.ComputeValue);
        FrameDependency first = Dependency("first"), second = Dependency("second"), third = Dependency("third");
        store.AddCachedRecursive(Eip8288Dependencies.Canonicalize([first, second, third]), [1]);
        store.AddCachedRecursive(Eip8288Dependencies.Canonicalize([second]), [2, 2]);
        store.AddCachedRecursive(Eip8288Dependencies.Canonicalize([second, Dependency("flood")]), [3]);
        for (int i = 0; i < 16; i++) store.AddCachedRecursive([Dependency($"unrelated:{i}")], [4]);
        InclusionListProofService service = new(verifier, store);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(service.ChooseHeldProof([[second], [first], [third]], Eip8288Prototype.Instance)?.StarkProof,
                Is.EqualTo(new byte[] { 2, 2 }), "the smallest proof covering the first candidate");
            Assert.That(service.ChooseHeldProof([[Dependency("unheld")], [first], [second]], Eip8288Prototype.Instance)?.StarkProof,
                Is.EqualTo(new byte[] { 1 }), "a candidate without a held proof passes to the next");
            Assert.That(verifier.VerificationCalls, Is.EqualTo(2));
        }
    }

    // EIPs#12475 (a95c12ff): the serialized InclusionListProof container, two offsets and both fields, plus its added four-byte
    // InclusionList offset, must fit MAX_IL_PROOF_BYTES.
    [TestCase(0, true)]
    [TestCase(1, false)]
    public void Held_proof_choice_respects_max_il_proof_bytes(int overLimit, bool chosen)
    {
        LeanProofStore store = new(FrameTxSigHash.ComputeValue);
        FrameDependency dependency = Dependency("sized");
        int proofBytes = Eip8288Constants.MaxIlProofBytes - 3 * sizeof(uint) - Eip8288Constants.DependencyTripleLength + overLimit;
        store.AddCachedRecursive([dependency], new byte[proofBytes]);

        HeldInclusionListProof? proof = new InclusionListProofService(new FakeLeanProofVerifier(true), store)
            .ChooseHeldProof([[dependency]], Eip8288Prototype.Instance);

        Assert.That(proof, chosen ? Is.Not.Null : Is.Null);
        if (chosen) Assert.That(new Producers.RecursiveStarkV1 { StarkProof = proof!.StarkProof, ProofDeps = proof.ProofDeps }.SszLength,
            Is.EqualTo(Eip8288Constants.MaxIlProofBytes));
    }

    // engine_getInclusionListV2 has a 1 s timeout, so held proofs whose verdicts are not cached are verified a bounded number of
    // times per list.
    [Test]
    public void Held_proof_choice_bounds_verifications()
    {
        FakeLeanProofVerifier verifier = new(false);
        LeanProofStore store = new(FrameTxSigHash.ComputeValue);
        List<IReadOnlyList<FrameDependency>> candidates = [];
        for (int i = 0; i < InclusionListProofService.MaxHeldProofVerifications + 2; i++)
        {
            FrameDependency candidate = Dependency($"bounded:{i}");
            store.AddCachedRecursive([candidate], [(byte)i]);
            candidates.Add([candidate]);
        }

        Assert.That(new InclusionListProofService(verifier, store).ChooseHeldProof(candidates, Eip8288Prototype.Instance), Is.Null);
        Assert.That(verifier.VerificationCalls, Is.EqualTo(InclusionListProofService.MaxHeldProofVerifications));
    }

    private static FrameDependency Dependency(string message) => new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute(message), default);

    private static byte[][] Encode(IReadOnlyList<FrameDependency> dependencies) =>
        [.. dependencies.Select(static dependency => Eip8288Dependencies.Serialize([dependency]))];

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
