// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.ProofAggregation;

public class RecursiveStarkAggregatorTests
{
    private static readonly ILeanProofVerifier Accepting = new FakeLeanProofVerifier(true);
    private static readonly ILeanProofVerifier Rejecting = new FakeLeanProofVerifier(false);

    private static FrameDependency Sphincs(string tag) =>
        new(Eip8288Constants.LeanSphincsScheme, Keccak.Compute(tag), Keccak.Compute(tag + "-vk"));

    [Test]
    public void Aggregate_dedups_and_commits_to_filtered_set()
    {
        FrameDependency a = Sphincs("a");
        FrameDependency b = Sphincs("b");
        AggregationInput input = new()
        {
            Deps = [a, b, a],
            Witnesses = [new byte[] { 1 }, new byte[] { 1 }, new byte[] { 1 }],
        };

        bool ok = RecursiveStarkAggregatorTests.TryAggregate(input, Accepting, Eip8288Verifier.Pinned, out IReadOnlyList<FrameDependency> filtered, out ValueHash256 depsHash);

        Assert.That(ok, Is.True);
        Assert.That(filtered, Is.EqualTo(Eip8288Dependencies.Canonicalize([a, b])));
        Assert.That(depsHash, Is.EqualTo(Eip8288Dependencies.ComputeDepsHash(new[] { a, b })));
    }

    [Test]
    public void Aggregate_removes_discards()
    {
        FrameDependency a = Sphincs("a");
        FrameDependency b = Sphincs("b");
        AggregationInput input = new()
        {
            Deps = [a, b],
            Witnesses = [new byte[] { 1 }, new byte[] { 1 }],
            Discards = [a],
        };

        RecursiveStarkAggregatorTests.TryAggregate(input, Accepting, Eip8288Verifier.Pinned, out IReadOnlyList<FrameDependency> filtered, out _);

        Assert.That(filtered, Is.EqualTo(new[] { b }));
    }

    [Test]
    public void Aggregate_folds_in_recursive_proof_dependencies()
    {
        FrameDependency a = Sphincs("a");
        FrameDependency b = Sphincs("b");
        AggregationInput input = new()
        {
            Deps = [a],
            Witnesses = [new byte[] { 1 }],
            RecursiveProofs = [new RecursiveProofInput([b], [9])],
        };

        RecursiveStarkAggregatorTests.TryAggregate(input, Accepting, Eip8288Verifier.Pinned, out IReadOnlyList<FrameDependency> filtered, out _);

        Assert.That(filtered, Is.EqualTo(Eip8288Dependencies.Canonicalize([a, b])));
    }

    [Test]
    public void Aggregate_fails_on_witness_count_mismatch()
    {
        AggregationInput input = new() { Deps = [Sphincs("a")], Witnesses = [] };

        Assert.That(RecursiveStarkAggregatorTests.TryAggregate(input, Accepting, Eip8288Verifier.Pinned, out _, out _), Is.False);
    }

    [Test]
    public void Aggregate_fails_when_verifier_rejects()
    {
        AggregationInput input = new() { Deps = [Sphincs("a")], Witnesses = [new byte[] { 1 }] };

        Assert.That(RecursiveStarkAggregatorTests.TryAggregate(input, Rejecting, Eip8288Verifier.Pinned, out _, out _), Is.False);
    }

    // EIP-8288 Test Case 10: a discard absent from the verified input union is rejected, whatever its scheme.
    [TestCase("direct")]
    [TestCase("recursive")]
    [TestCase("discard")]
    public void Aggregate_rejects_unknown_schemes_and_discards_outside_the_inputs(string list)
    {
        FrameDependency a = Sphincs("a");
        FrameDependency unknown = new(0x12, a.DataHash, a.VerificationKey);
        AggregationInput input = list switch
        {
            "direct" => new() { Deps = [unknown], Witnesses = [new byte[] { 1 }] },
            "recursive" => new() { RecursiveProofs = [new RecursiveProofInput([a, unknown], [9])], Discards = [unknown] },
            _ => new() { Deps = [a], Witnesses = [new byte[] { 1 }], Discards = [unknown] },
        };

        Assert.That(RecursiveStarkAggregatorTests.TryAggregate(input, Accepting, Eip8288Verifier.Pinned, out _, out _), Is.False);
    }

    [Test]
    public void Aggregate_rejects_a_canonical_enabled_discard_absent_from_the_inputs()
    {
        FrameDependency a = Sphincs("a"), b = Sphincs("b"), c = Sphincs("c");
        AggregationInput input = new() { Deps = [a, b], Witnesses = [new byte[] { 1 }, new byte[] { 1 }], Discards = [a, c] };

        Assert.That(RecursiveStarkAggregatorTests.TryAggregate(input, Accepting, Eip8288Verifier.Pinned, out _, out _), Is.False);
    }

    // get_deps_hash omits the scheme, so a reserved leanSTARK entry is rejected in every list the circuit reads.
    [Test]
    public void Aggregate_rejects_reserved_leanstark_in_direct_recursive_and_discard_lists()
    {
        FrameDependency sphincs = Sphincs("a");
        FrameDependency stark = new(Eip8288Constants.LeanStarkScheme, sphincs.DataHash, sphincs.VerificationKey);
        AggregationInput[] inputs =
        [
            new() { Deps = [stark], Witnesses = [new byte[] { 1 }] },
            new() { RecursiveProofs = [new RecursiveProofInput([sphincs, stark], [9])] },
            new() { RecursiveProofs = [new RecursiveProofInput([sphincs], [9])], Discards = [stark] },
        ];
        using (Assert.EnterMultipleScope())
        {
            foreach (AggregationInput input in inputs)
                Assert.That(RecursiveStarkAggregatorTests.TryAggregate(input, Accepting, Eip8288Verifier.Pinned, out _, out _), Is.False);
        }
    }

    [Test]
    public void Proof_store_prunes_recursive_dependencies_and_copies_witnesses([Values] bool recursive)
    {
        FrameDependency a = Sphincs("a");
        FrameDependency b = Sphincs("b");
        byte[] first = [1];
        LeanProofStore store = new(FrameTxSigHash.ComputeValue);
        store.AddVerified([a, b], recursive ? null : [first, [2]], recursive ? first : null);
        first[0] = 9;

        Assert.That(store.TryGetInput([b], out AggregationInput input), Is.True);
        Assert.That(RecursiveStarkAggregatorTests.TryAggregate(input, Accepting, Eip8288Verifier.Pinned, out IReadOnlyList<FrameDependency> deps, out _), Is.True);
        Assert.That(deps, Is.EqualTo(new[] { b }));
        if (recursive)
        {
            Assert.That(input.Discards, Is.EqualTo(new[] { a }));
            Assert.That(input.RecursiveProofs[0].Proof.Span[0], Is.EqualTo(1));
        }
        else
        {
            Assert.That(input.Deps, Is.EqualTo(new[] { b }));
            Assert.That(input.Witnesses[0].ToArray(), Is.EqualTo(new byte[] { 2 }));
        }
    }

    [Test]
    public void Discard_set_keeps_dependencies_shared_with_retained_transactions()
    {
        FrameDependency a = Sphincs("a"), b = Sphincs("b"), c = Sphincs("c");
        // A removed transaction also declared b; a retained one still needs it.
        Assert.That(Eip8288Dependencies.DiscardDependencies([c, a, b, b], [b, c]), Is.EqualTo(new[] { a }));
        Assert.That(Eip8288Dependencies.DiscardDependencies([a, b], [a, b]), Is.Empty);
        Assert.That(() => Eip8288Dependencies.DiscardDependencies([a, b], [b, Sphincs("d")]), Throws.ArgumentException,
            "retained dependencies absent from the inputs need another proof");
    }

    // EIPs#12473: input_deps includes dependencies covered only by mode-0 direct witnesses.
    [Test]
    public void Direct_witness_dependencies_count_as_discard_inputs()
    {
        FrameDependency a = Sphincs("a"), b = Sphincs("b"), c = Sphincs("c");
        AggregationInput input = new()
        {
            Deps = [a],
            Witnesses = [new byte[] { 1 }],
            RecursiveProofs = [new RecursiveProofInput([b, c], [9])],
            Discards = Eip8288Dependencies.DiscardDependencies([a, b, c], [b, c])
        };
        AggregationInput combined = RecursiveStarkAggregator.Combine(
            [new() { Deps = [c], Witnesses = [new byte[] { 1 }] }, new() { RecursiveProofs = [new RecursiveProofInput([a, b], [9])] }], [b, c]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(input.Discards, Is.EqualTo(new[] { a }));
            Assert.That(RecursiveStarkAggregatorTests.TryAggregate(input, Accepting, Eip8288Verifier.Pinned, out IReadOnlyList<FrameDependency> deps, out _), Is.True);
            Assert.That(deps, Is.EqualTo(Eip8288Dependencies.Canonicalize([b, c])));
            Assert.That(combined.Deps, Is.EqualTo(new[] { c }), "a retained dependency covered only by a direct witness is an input");
            Assert.That(combined.Discards, Is.EqualTo(new[] { a }));
        }
    }

    [Test]
    public void Pruning_a_recursive_input_discards_only_dependencies_no_retained_transaction_needs()
    {
        FrameDependency a = Sphincs("a"), b = Sphincs("b"), c = Sphincs("c");
        LeanProofStore store = new(FrameTxSigHash.ComputeValue);
        store.AddVerified([a, b, c], null, [1]);
        Assert.That(store.TryGetInput([c, b], out AggregationInput input), Is.True);
        AggregationInput combined = RecursiveStarkAggregator.Combine([input], [b, c]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(input.Discards, Is.EqualTo(new[] { a }));
            Assert.That(combined.Discards, Is.EqualTo(new[] { a }));
            Assert.That(RecursiveStarkAggregatorTests.TryAggregate(input, Accepting, Eip8288Verifier.Pinned, out IReadOnlyList<FrameDependency> deps, out _), Is.True);
            Assert.That(deps, Is.EqualTo(Eip8288Dependencies.Canonicalize([b, c])));
        }
    }

    [Test]
    public void Proof_store_rejects_missing_dependencies()
    {
        LeanProofStore store = new(FrameTxSigHash.ComputeValue);
        Assert.That(store.TryGetInput([Sphincs("missing")], out _), Is.False);
        Assert.That(store.TryGetInput([], out AggregationInput input), Is.True);
        Assert.That(input.Deps, Is.Empty);
    }
    [Test]
    public void Cancellation_after_one_native_batch_stops_the_remaining_tree()
    {
        using System.Threading.CancellationTokenSource cancellation = new();
        FakeLeanProofVerifier verifier = new(true) { OnProving = cancellation.Cancel };
        FrameDependency[] dependencies = new FrameDependency[8];
        List<ReadOnlyMemory<byte>> witnesses = [];
        for (int i = 0; i < dependencies.Length; i++)
        {
            dependencies[i] = Sphincs(i.ToString());
            witnesses.Add(new byte[] { 1 });
        }
        AggregationInput input = new() { Deps = dependencies, Witnesses = witnesses };
        ValueHash256 hash = Eip8288Dependencies.ComputeDepsHash(dependencies);
        Assert.Throws<OperationCanceledException>(() => RecursiveStarkAggregator.Prove(input, verifier, Eip8288Verifier.Pinned, hash, cancellation.Token));
        Assert.That(verifier.ProofCalls, Is.EqualTo(1));
    }

    [Test]
    public void Unchanged_parent_is_verified_and_reused_without_proving()
    {
        FrameDependency a = Sphincs("unchanged-a"), b = Sphincs("unchanged-b");
        byte[] parent = [2, 3];
        AggregationInput input = new() { RecursiveProofs = [new([b, a], parent)] };
        FakeLeanProofVerifier verifier = new(true);
        ValueHash256 hash = Eip8288Dependencies.ComputeDepsHash([a, b]);

        byte[] proof = RecursiveStarkAggregator.Prove(input, verifier, Eip8288Verifier.Pinned, hash);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(proof, Is.EqualTo(parent));
            Assert.That(proof, Is.Not.SameAs(parent));
            Assert.That(verifier.VerificationCalls, Is.EqualTo(1));
            Assert.That(verifier.ProofCalls, Is.Zero);
        }
        proof[0] = 9;
        Assert.That(parent[0], Is.EqualTo(2));
    }

    public enum StatementChange { DirectWitness, Discard, TargetHash, ExtraParent }

    [Test]
    public void Changed_statement_still_requires_proving([Values] StatementChange change)
    {
        FrameDependency a = Sphincs("parent-a"), b = Sphincs("parent-b");
        AggregationInput input = new()
        {
            Deps = change == StatementChange.DirectWitness ? [b] : [],
            Witnesses = change == StatementChange.DirectWitness ? [new byte[] { 1 }] : [],
            Discards = change == StatementChange.Discard ? [a] : [],
            RecursiveProofs = change == StatementChange.ExtraParent ? [new([a], [2]), new([b], [3])] : [new([a], [2])]
        };
        ValueHash256 hash = Eip8288Dependencies.ComputeDepsHash(change == StatementChange.TargetHash ? [b] : [a]);
        FakeLeanProofVerifier verifier = new(true);

        RecursiveStarkAggregator.Prove(input, verifier, Eip8288Verifier.Pinned, hash);

        Assert.That(verifier.ProofCalls, Is.EqualTo(1));
    }

    [Test]
    public void Unchanged_parent_cannot_bypass_failed_verification()
    {
        FrameDependency dependency = Sphincs("invalid-parent");
        ValueHash256 hash = Eip8288Dependencies.ComputeDepsHash([dependency]);
        FakeLeanProofVerifier verifier = new(false);
        AggregationInput input = new() { RecursiveProofs = [new([dependency], [2])] };

        Assert.Throws<InvalidOperationException>(() => RecursiveStarkAggregator.Prove(input, verifier, Eip8288Verifier.Pinned, hash));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(verifier.VerificationCalls, Is.EqualTo(1));
            Assert.That(verifier.ProofCalls, Is.Zero);
        }
    }

    [Test]
    public void Cancellation_stops_unchanged_parent_reuse([Values] bool beforeVerification)
    {
        using System.Threading.CancellationTokenSource cancellation = new();
        FakeLeanProofVerifier verifier = new(true) { OnVerification = cancellation.Cancel };
        FrameDependency dependency = Sphincs("canceled-parent");
        ValueHash256 hash = Eip8288Dependencies.ComputeDepsHash([dependency]);
        AggregationInput input = new() { RecursiveProofs = [new([dependency], [2])] };
        if (beforeVerification) cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => RecursiveStarkAggregator.Prove(input, verifier, Eip8288Verifier.Pinned, hash, cancellation.Token));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(verifier.VerificationCalls, Is.EqualTo(beforeVerification ? 0 : 1));
            Assert.That(verifier.ProofCalls, Is.Zero);
        }
    }

    [Test]
    public void Prover_hierarchically_folds_more_than_sixteen_recursive_children()
    {
        List<RecursiveProofInput> recursive = [];
        List<FrameDependency> deps = [];
        for (int i = 0; i < 33; i++)
        {
            FrameDependency dependency = Sphincs(i.ToString());
            recursive.Add(new([dependency], [1]));
            deps.Add(dependency);
        }
        FakeLeanProofVerifier verifier = new(true);
        ValueHash256 hash = Eip8288Dependencies.ComputeDepsHash(deps);
        byte[] proof = RecursiveStarkAggregator.Prove(new() { RecursiveProofs = recursive }, verifier, Eip8288Verifier.Pinned, in hash);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(proof, Is.EqualTo(new byte[] { 1 }));
            Assert.That(verifier.LargestRecursiveInput, Is.EqualTo(2));
            Assert.That(verifier.ProofCalls, Is.GreaterThan(1));
        }
    }

    // EIPs#12475: a builder may merge the running aggregate with every inclusion-list proof, discarding what its block does not
    // need. The prototype guest folds two inputs per step, so the managed tree merges them over several bounded steps.
    [Test]
    public void Committee_fan_in_folds_every_inclusion_list_proof_and_the_running_aggregate()
    {
        List<RecursiveProofInput> recursive = [];
        List<FrameDependency> deps = [];
        for (int i = 0; i <= Eip7805Constants.InclusionListCommitteeSize; i++)
        {
            FrameDependency dependency = Sphincs($"list{i}");
            recursive.Add(new([dependency], [1]));
            deps.Add(dependency);
        }
        // The last list is discarded entirely, as a builder may exclude any inclusion-list transaction.
        FakeLeanProofVerifier verifier = new(true);
        ValueHash256 hash = Eip8288Dependencies.ComputeDepsHash(deps.GetRange(0, deps.Count - 1));
        byte[] proof = RecursiveStarkAggregator.Prove(new() { RecursiveProofs = recursive, Discards = [deps[^1]] }, verifier, Eip8288Verifier.Pinned, in hash);
        Assert.That(proof, Is.EqualTo(new byte[] { 1 }));
    }

    [Test]
    public void Hierarchical_children_prune_discarded_claims_before_the_native_count_limit()
    {
        FrameDependency[] dependencies = new FrameDependency[Eip8288Constants.MaxProofDependencies + 1];
        RecursiveProofInput[] children = new RecursiveProofInput[dependencies.Length];
        for (int i = 0; i < children.Length; i++)
        {
            dependencies[i] = Sphincs(i.ToString());
            children[i] = new([dependencies[i]], LeanProofTestEnvelope.Create([dependencies[i]]));
        }
        CountBoundedVerifier verifier = new();
        ValueHash256 hash = Eip8288Dependencies.ComputeDepsHash([dependencies[0]]);
        byte[] proof = RecursiveStarkAggregator.Prove(new()
        {
            RecursiveProofs = children,
            Discards = dependencies[1..]
        }, verifier, Eip8288Verifier.Pinned, hash);
        Assert.That(proof, Is.EqualTo(hash.ToByteArray()));
        // Children whose every claim is discarded are dropped rather than reproven to an empty set.
        Assert.That(verifier.ProofCalls, Is.EqualTo(1));
    }

    [Test]
    public void Recursive_coverage_avoids_direct_reproving([Values(1, 2, 3)] int directWitnessBytes)
    {
        FrameDependency dependency = Sphincs("recursive-source-tie");
        byte[] parent = LeanProofTestEnvelope.Create([dependency], 2);
        byte[] directWitness = new byte[directWitnessBytes];
        AggregationInput input = new()
        {
            Deps = [dependency, dependency, dependency, dependency, dependency],
            Witnesses = [directWitness, directWitness, directWitness, directWitness, directWitness],
            RecursiveProofs = [new([dependency], parent)]
        };
        EnvelopeBoundedVerifier verifier = new();
        ValueHash256 hash = Eip8288Dependencies.ComputeDepsHash([dependency]);

        byte[] result = RecursiveStarkAggregator.Prove(input, verifier, Eip8288Verifier.Pinned, hash);

        Assert.That(verifier.VerifyRecursiveStark(hash, Eip8288Constants.AggregatedVk, result), Is.True);
        int directProves = 0;
        int parentPrunes = 0;
        foreach (AggregationInput provingInput in verifier.ProvingInputs)
        {
            if (provingInput.Deps.Count != 0) directProves++;
            if (provingInput.Discards.Count != 0) parentPrunes++;
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(directProves, Is.EqualTo(0));
            Assert.That(parentPrunes, Is.EqualTo(0));
            Assert.That(verifier.DirectVerifications, Is.EqualTo(input.Deps.Count), "omitted direct witnesses still require authentication");
            Assert.That(verifier.VerifiedProofs, Does.Contain(ValueKeccak.Compute(parent)));
        }
    }

    [Test]
    public void Large_direct_witnesses_use_separate_leaves_before_recursive_union()
    {
        FrameDependency a = Sphincs("large-a");
        FrameDependency b = Sphincs("large-b");
        byte[] witness = new byte[3 * 1024 * 1024];
        AggregationInput input = new() { Deps = [a, b], Witnesses = [witness, witness] };
        ValueHash256 hash = Eip8288Dependencies.ComputeDepsHash([a, b]);
        EnvelopeBoundedVerifier verifier = new();

        byte[] proof = RecursiveStarkAggregator.Prove(input, verifier, Eip8288Verifier.Pinned, hash);

        Assert.That(verifier.VerifyRecursiveStark(hash, Eip8288Constants.AggregatedVk, proof), Is.True);
        int directLeaves = 0;
        foreach (AggregationInput batch in verifier.ProvingInputs)
        {
            if (batch.Deps.Count == 0) continue;
            directLeaves++;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(batch.Deps, Has.Count.EqualTo(1));
                Assert.That(RecursiveStarkAggregator.InputSize(batch), Is.LessThanOrEqualTo(RecursiveStarkAggregator.MaxProductionWitnessBytes));
            }
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(directLeaves, Is.EqualTo(2));
            Assert.That(verifier.DirectVerifications, Is.EqualTo(2));
            Assert.That(verifier.ProvingInputs[^1].RecursiveProofs, Has.Count.EqualTo(2));
        }
    }

    [Test]
    public void Invalid_direct_duplicate_is_verified_before_it_is_omitted([Values] bool tiedRecursiveSource)
    {
        FrameDependency a = Sphincs("invalid-duplicate");
        AggregationInput input = new()
        {
            Deps = tiedRecursiveSource ? [a, a, a, a, a] : [a, a, Sphincs("one"), Sphincs("two"), Sphincs("three")],
            Witnesses = tiedRecursiveSource
                ? [new byte[] { 9, 0 }, new byte[] { 1, 0 }, new byte[] { 1, 0 }, new byte[] { 1, 0 }, new byte[] { 1, 0 }]
                : [new byte[] { 9, 0 }, new byte[] { 1 }, new byte[] { 1 }, new byte[] { 1 }, new byte[] { 1 }],
            RecursiveProofs = tiedRecursiveSource
                ? [new([a], LeanProofTestEnvelope.Create([a], 2))]
                : []
        };
        ValueHash256 hash = Eip8288Dependencies.ComputeDepsHash(input.Deps);
        Assert.Throws<InvalidOperationException>(() => RecursiveStarkAggregator.Prove(input, new EnvelopeBoundedVerifier(), Eip8288Verifier.Pinned, hash));
    }

    private sealed class EnvelopeBoundedVerifier : ILeanProofVerifier
    {
        public HashSet<ValueHash256> VerifiedProofs { get; } = [];
        public List<AggregationInput> ProvingInputs { get; } = [];
        public int DirectVerifications { get; private set; }
        public void EnsureAvailable() { }
        public bool VerifyLeanSphincs(in ValueHash256 hash, in ValueHash256 key, ReadOnlySpan<byte> witness)
        {
            DirectVerifications++;
            return witness.Length > 0 && witness[0] != 9;
        }
        public bool VerifyRecursiveStark(in ValueHash256 hash, ReadOnlySpan<byte> key, ReadOnlySpan<byte> proof)
        {
            VerifiedProofs.Add(ValueKeccak.Compute(proof));
            if (proof.Length < 8 || !proof[..4].SequenceEqual("NLTF"u8)) return false;
            int count = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(proof[4..]);
            if (count < 0 || count > Eip8288Constants.MaxProofDependencies
                || count > (proof.Length - 8) / Eip8288Constants.DependencyTripleLength) return false;
            return Eip8288Dependencies.ComputeDepsHash(Eip8288Dependencies.Parse(proof.Slice(8, count * Eip8288Constants.DependencyTripleLength))) == hash;
        }
        public byte[] ProveRecursiveStark(in ValueHash256 hash, ReadOnlySpan<byte> key, AggregationInput input)
        {
            ProvingInputs.Add(input);
            if (!RecursiveStarkAggregatorTests.TryAggregate(input, this, Eip8288Verifier.Pinned, out IReadOnlyList<FrameDependency> dependencies, out ValueHash256 actual))
                throw new InvalidOperationException("Invalid dependency input");
            Assert.That(actual, Is.EqualTo(hash));
            HashSet<FrameDependency> required = [.. dependencies];
            Assert.That(LeanProofCapacity.CapacityError(required), Is.Null);
            return LeanProofTestEnvelope.Create(dependencies);
        }
    }

    private sealed class CountBoundedVerifier : ILeanProofVerifier
    {
        public int ProofCalls { get; private set; }
        public void EnsureAvailable() { }
        public bool VerifyLeanSphincs(in ValueHash256 hash, in ValueHash256 key, ReadOnlySpan<byte> witness) => true;
        public bool VerifyRecursiveStark(in ValueHash256 hash, ReadOnlySpan<byte> key, ReadOnlySpan<byte> proof) => true;
        public byte[] ProveRecursiveStark(in ValueHash256 hash, ReadOnlySpan<byte> key, AggregationInput input)
        {
            Assert.That(RecursiveStarkAggregatorTests.TryAggregate(input, Accepting, Eip8288Verifier.Pinned, out IReadOnlyList<FrameDependency> dependencies, out ValueHash256 actual), Is.True);
            Assert.That(actual, Is.EqualTo(hash));
            Assert.That(dependencies.Count, Is.LessThanOrEqualTo(Eip8288Constants.MaxProofDependencies));
            ProofCalls++;
            return hash.ToByteArray();
        }
    }

    [Test]
    public void Proof_store_evicts_oldest_coverage_without_removing_newer_witnesses([Values] bool sameDependency)
    {
        LeanProofStore store = new(FrameTxSigHash.ComputeValue);
        FrameDependency first = Sphincs("0");
        byte[] witness = new byte[64 * 1024 - Eip8288Constants.DependencyTripleLength];
        for (int i = 0; i < 1025; i++) store.AddVerified([sameDependency ? first : Sphincs(i.ToString())], [witness], null);
        Assert.That(store.TryGetInput([first], out _), Is.EqualTo(sameDependency));
    }

    [Test]
    public void Empty_dependency_proofs_do_not_evict_useful_witnesses()
    {
        LeanProofStore store = new(FrameTxSigHash.ComputeValue);
        FrameDependency dependency = Sphincs("useful");
        store.AddVerified([dependency], [[1]], null);
        for (int i = 0; i < 1025; i++) store.AddVerified([], null, [2]);
        Assert.That(store.TryGetInput([dependency], out _), Is.True);
    }

    [Test]
    public void Repeated_identical_proofs_do_not_evict_other_coverage()
    {
        LeanProofStore store = new(FrameTxSigHash.ComputeValue);
        FrameDependency honest = Sphincs("honest");
        FrameDependency repeated = Sphincs("repeated");
        store.AddVerified([honest], [[1]], null);
        for (int i = 0; i < 1025; i++) store.AddVerified([repeated], null, [2]);
        Assert.That(store.TryGetInput([honest], out _), Is.True);
        Assert.That(store.TryGetRecursiveProof([repeated], out byte[]? proof), Is.True);
        Assert.That(proof, Is.EqualTo(new byte[] { 2 }));
        Assert.That(store.TryGetRecursiveProof([honest, repeated], out _), Is.False);
    }

    [Test]
    public void New_proof_identities_for_pending_coverage_do_not_evict_other_transactions()
    {
        LeanProofStore store = new(FrameTxSigHash.ComputeValue);
        FrameDependency honest = Sphincs("honest");
        FrameDependency pending = Sphincs("pending");
        store.AddVerified([honest], [[1]], null);
        store.AddVerified([pending], null, [2]);
        for (int i = 0; i < 1025; i++)
            store.AddVerified([pending], null, BitConverter.GetBytes(i), admittedDependencies: [pending]);
        Assert.That(store.TryGetInput([honest, pending], out _), Is.True);
        Assert.That(store.TryGetRecursiveProof([pending], out byte[]? proof), Is.True);
        Assert.That(proof, Is.EqualTo(new byte[] { 2 }));
    }

    [Test]
    public void Candidate_snapshots_share_owned_read_only_witness_buffers([Values] bool recursive)
    {
        LeanProofStore store = new(FrameTxSigHash.ComputeValue);
        FrameDependency dependency = Sphincs("large");
        byte[] proof = new byte[LeanProofStore.MaxWrapperBytes - 1024];
        store.AddVerified([dependency], recursive ? null : [proof], recursive ? proof : null);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
            Assert.That(store.TryGetInput([dependency], out _), Is.True);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.That(allocated, Is.LessThan(1024 * 1024));
    }

    [Test]
    public void Uninitialized_recursive_inputs_report_an_argument_error()
    {
        AggregationInput input = new() { RecursiveProofs = [default] };
        Assert.Throws<ArgumentException>(() => RecursiveStarkAggregator.Prove(input, Accepting, Eip8288Verifier.Pinned, default));
        Assert.Throws<ArgumentException>(() => RecursiveStarkAggregatorTests.TryAggregate(input, Accepting, Eip8288Verifier.Pinned, out _, out _));
        Assert.Throws<ArgumentException>(() => RecursiveStarkAggregator.Combine([input], []));
        Assert.Throws<ArgumentException>(() => RecursiveStarkAggregator.InputSize(input));
    }

    [Test]
    public async Task Disposed_admission_scope_revokes_captured_execution_context()
    {
        LeanProofStore store = new(FrameTxSigHash.ComputeValue);
        FrameDependency dependency = Sphincs("temporary");
        Transaction transaction = new()
        {
            Type = TxType.FrameTx,
            Frames = [new(FrameMode.DepVerify, FrameFlags.None, null, Eip8288Constants.LeanSphincsVerificationGas,
                Nethermind.Int256.UInt256.Zero, Eip8288Dependencies.Serialize([dependency]))]
        };
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> captured;
        Assert.That(store.TryBeginAdmission([dependency], [[1]], null, out IDisposable? admission), Is.True);
        using (admission)
        {
            Assert.That(store.Covers(transaction), Is.True);
            captured = Task.Run(async () => { await release.Task; return store.Covers(transaction); });
        }
        release.SetResult();
        Assert.That(await captured, Is.False);
        Assert.That(store.Covers(transaction), Is.False);
    }

    [Test]
    public void Admission_reserves_capacity_and_pending_coverage_survives_unique_proof_floods()
    {
        LeanProofStore store = new(FrameTxSigHash.ComputeValue);
        List<Transaction> pending = [];
        byte[] witness = new byte[64 * 1024 - Eip8288Constants.DependencyTripleLength];
        for (int i = 0; i < 1024; i++)
        {
            FrameDependency dependency = Sphincs($"pending:{i}");
            Transaction transaction = DependencyTransaction(dependency);
            transaction.SenderAddress = new Address(dependency.DataHash.Bytes[..20]);
            Assert.That(store.TryBeginAdmission([dependency], [witness], null, out IDisposable? admission), Is.True);
            using (admission) Assert.That(store.PinPending(transaction), Is.True);
            pending.Add(transaction);
        }
        FrameDependency newcomer = Sphincs("newcomer");
        Assert.That(store.TryBeginAdmission([newcomer], [[1]], null, out _), Is.False);
        for (int i = 0; i < 20; i++) store.AddVerified([Sphincs($"flood:{i}")], [[1]], null);
        foreach (Transaction transaction in pending) Assert.That(store.Covers(transaction), Is.True);
        store.UnpinPending(pending[0].Hash!.ValueHash256);
        Assert.That(store.TryBeginAdmission([newcomer], [[1]], null, out IDisposable? retry), Is.True);
        using (retry) store.PinPending(DependencyTransaction(newcomer));
        Assert.That(store.Covers(pending[0]), Is.False);
        Assert.That(store.Covers(pending[1]), Is.True);
    }

    [Test]
    public void Overlapping_partial_admission_and_cache_eviction_preserve_original_pending_witnesses()
    {
        LeanProofStore store = new(FrameTxSigHash.ComputeValue);
        FrameDependency a = Sphincs("pending-a"), b = Sphincs("pending-b");
        Transaction original = DependencyTransaction(a), later = DependencyTransaction(b);
        store.AddVerified([a], [[1]], null);
        store.PinPending(original);
        Assert.That(store.TryBeginAdmission([a, b], null, [2], out IDisposable? admission), Is.True);
        using (admission)
        {
            store.PinPending(later);
            store.AddVerified([a, b], null, [2], admittedDependencies: [b]);
        }
        store.UnpinPending(later.Hash!.ValueHash256);
        byte[] witness = new byte[64 * 1024];
        for (int i = 0; i < 1100; i++)
        {
            store.AddVerified([Sphincs($"coverage:{i}")], [witness], null);
            store.AddCachedRecursive([Sphincs($"cache:{i}")], [3]);
        }
        Assert.That(store.TryGetInput([a], out AggregationInput input), Is.True);
        Assert.That(input.Witnesses[0].Span[0], Is.EqualTo(1), "the unpinned overlapping parent must not replace pending coverage");
        Assert.That(store.Covers(later), Is.False);
    }

    [Test]
    public void Rejected_reservations_release_capacity_without_publishing_coverage()
    {
        LeanProofStore store = new(FrameTxSigHash.ComputeValue);
        for (int i = 0; i < 1100; i++)
        {
            FrameDependency dependency = Sphincs($"rejected:{i}");
            Assert.That(store.TryBeginAdmission([dependency], [[1]], null, out IDisposable? admission), Is.True);
            admission!.Dispose();
            Assert.That(store.Covers(DependencyTransaction(dependency)), Is.False);
        }
    }

    [Test]
    public void Partial_direct_admission_releases_large_rejected_witnesses()
    {
        LeanProofStore store = new(FrameTxSigHash.ComputeValue);
        List<Transaction> pending = [];
        // Enough near-maximum rejected witnesses to exceed the shared store several times over if they were retained.
        byte[] rejectedWitness = new byte[LeanProofStore.MaxWrapperBytes - 1024];
        for (int i = 0; i < 40; i++)
        {
            FrameDependency accepted = Sphincs($"accepted:{i}"), rejected = Sphincs($"rejected:{i}");
            Transaction transaction = DependencyTransaction(accepted);
            Assert.That(store.TryBeginAdmission([accepted, rejected], [[1], rejectedWitness], null, out IDisposable? admission), Is.True);
            using (admission)
            {
                Assert.That(store.PinPending(transaction), Is.True);
                store.CommitAdmission([accepted]);
            }
            Assert.That(store.Covers(DependencyTransaction(rejected)), Is.False);
            pending.Add(transaction);
        }
        foreach (Transaction transaction in pending) Assert.That(store.Covers(transaction), Is.True);
    }

    [Test]
    public void Rejected_pool_insertion_releases_unpublished_direct_witnesses()
    {
        LeanProofStore store = new(FrameTxSigHash.ComputeValue);
        FrameDependency dependency = Sphincs("rejected-insertion");
        Transaction transaction = DependencyTransaction(dependency);
        Assert.That(store.TryBeginAdmission([dependency], [[1]], null, out IDisposable? admission), Is.True);
        using (admission)
        {
            Assert.That(store.PinPending(transaction, publish: false), Is.True);
            store.UnpinPending(transaction.Hash!.ValueHash256);
        }
        Assert.That(store.Covers(transaction), Is.False);
    }

    [Test]
    public void Sender_quota_counts_shared_recursive_records_once_and_releases_the_last_owner()
    {
        LeanProofStore store = new(FrameTxSigHash.ComputeValue);
        // Records are bounded by MAX_WRAPPER_BYTES, so the sender quota holds a few near-maximum ones.
        byte[] proof = new byte[LeanProofStore.MaxWrapperBytes - 1024];
        int fitting = (int)(LeanProofStore.MaxSenderPinnedBytes / proof.Length);
        FrameDependency[] dependencies = [.. Enumerable.Range(0, fitting).Select(i => Sphincs($"quota-{i}"))];
        FrameDependency c = Sphincs("quota-c");
        foreach (FrameDependency dependency in dependencies) store.AddVerified([dependency], null, proof);
        store.AddVerified([c], null, proof);
        Transaction first = DependencyTransaction(dependencies[0]), shared = DependencyTransaction(dependencies[0]);
        shared.Hash = Keccak.Compute("shared-transaction");
        Assert.That(store.PinPending(first), Is.True);
        Assert.That(store.PinPending(shared), Is.True, "the same proof is charged once for this sender");
        foreach (FrameDependency dependency in dependencies[1..]) Assert.That(store.PinPending(DependencyTransaction(dependency)), Is.True);
        Assert.That(store.TryReserveTransaction([c], out _, Address.Zero), Is.False);
        Address other = new("0x0000000000000000000000000000000000000001");
        Assert.That(store.TryReserveTransaction([c], out IDisposable? anotherSender, other), Is.True);
        anotherSender!.Dispose();
        store.UnpinPending(first.Hash!.ValueHash256);
        Assert.That(store.TryReserveTransaction([c], out _, Address.Zero), Is.False);
        store.UnpinPending(shared.Hash!.ValueHash256);
        Assert.That(store.TryReserveTransaction([c], out IDisposable? retry, Address.Zero), Is.True);
        retry!.Dispose();
        Assert.That(store.PinPending(DependencyTransaction(Sphincs("uncovered"))), Is.False);
    }

    private static Transaction DependencyTransaction(FrameDependency dependency) => new()
    {
        Type = TxType.FrameTx,
        Hash = new Hash256(dependency.DataHash),
        SenderAddress = Address.Zero,
        Frames = [new(FrameMode.DepVerify, FrameFlags.None, null, Eip8288Constants.LeanSphincsVerificationGas,
            Nethermind.Int256.UInt256.Zero, Eip8288Dependencies.Serialize([dependency]))]
    };

    [Test]
    public void Recursive_input_snapshots_caller_owned_buffers()
    {
        FrameDependency[] dependencies = [Sphincs("a")];
        byte[] bytes = [1];
        RecursiveProofInput input = new(dependencies, bytes);
        bytes[0] = 2;
        dependencies[0] = Sphincs("b");
        Assert.That(input.Proof.Span[0], Is.EqualTo(1));
        Assert.That(input.ProofHash, Is.EqualTo(ValueKeccak.Compute(input.Proof.Span)));
        Assert.That(input.InnerDeps[0], Is.EqualTo(Sphincs("a")));
    }

    [Test]
    public void Combining_shared_dependencies_retains_one_witness_and_one_identical_parent()
    {
        FrameDependency a = Sphincs("a");
        FrameDependency b = Sphincs("b");
        AggregationInput input = new() { Deps = [a], Witnesses = [new byte[] { 1 }], RecursiveProofs = [new([b], [2])] };
        AggregationInput combined = RecursiveStarkAggregator.Combine([input, input, input], [a, b]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(combined.Deps, Has.Count.EqualTo(1));
            Assert.That(combined.Witnesses, Has.Count.EqualTo(1));
            Assert.That(combined.RecursiveProofs, Has.Count.EqualTo(1));
            Assert.That(combined.Discards, Is.Empty);
            Assert.That(RecursiveStarkAggregator.InputSize(combined), Is.EqualTo(RecursiveStarkAggregator.InputSize(input)));
        }
    }

    // EIP-8288 discard_dependencies: the FOCIL proof covers {a, b}; the omitted entry declared a and b, the included
    // one declares a, so only b leaves the claim.
    [Test]
    public void Pruning_an_omitted_focil_entry_retains_dependencies_shared_with_included_transactions()
    {
        FrameDependency a = Sphincs("a");
        FrameDependency b = Sphincs("b");
        AggregationInput focil = new() { RecursiveProofs = [new([a, b], [1])] };
        AggregationInput included = new() { Deps = [a], Witnesses = [new byte[] { 2 }] };
        AggregationInput combined = RecursiveStarkAggregator.Combine([focil, included], [a]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(combined.Discards, Is.EqualTo(new[] { b }));
            Assert.That(combined.Deps, Is.Empty);
            Assert.That(combined.RecursiveProofs, Has.Count.EqualTo(1));
            Assert.That(RecursiveStarkAggregatorTests.TryAggregate(combined, Accepting, Eip8288Verifier.Pinned, out IReadOnlyList<FrameDependency> proven, out _), Is.True);
            Assert.That(proven, Is.EqualTo(new[] { a }));
        }
    }

    // EIP-8288 MAX_AGGREGATE_PROOF_BYTES: a proof at the bound still needs full verification; one byte longer is rejected
    // before the verifier runs.
    [TestCase(Eip8288Constants.MaxAggregateProofBytes, true, 1)]
    [TestCase(Eip8288Constants.MaxAggregateProofBytes, false, 1)]
    [TestCase(Eip8288Constants.MaxAggregateProofBytes + 1, true, 0)]
    public void Stark_check_bounds_the_proof_before_verification(int length, bool verdict, int expectedCalls)
    {
        FakeLeanProofVerifier verifier = new(verdict);
        ValueHash256 depsHash = Eip8288Dependencies.ComputeDepsHash([Sphincs("a")]);
        bool valid = RecursiveStarkAggregator.VerifyStarkCheck(verifier, Eip8288Verifier.Pinned, 1, in depsHash, new byte[length]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(valid, Is.EqualTo(verdict && expectedCalls == 1));
            Assert.That(verifier.VerificationCalls, Is.EqualTo(expectedCalls));
        }
    }

    [TestCase(Eip8288Constants.MaxAggregateProofBytes, true)]
    [TestCase(Eip8288Constants.MaxAggregateProofBytes + 1, false)]
    public void Recursive_input_above_the_aggregate_proof_bound_fails_before_its_verifier_runs(int length, bool expected)
    {
        FakeLeanProofVerifier verifier = new(true);
        AggregationInput input = new() { RecursiveProofs = [new RecursiveProofInput([Sphincs("a")], new byte[length])] };
        bool valid = RecursiveStarkAggregatorTests.TryAggregate(input, verifier, Eip8288Verifier.Pinned, out _, out _);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(valid, Is.EqualTo(expected));
            Assert.That(verifier.VerificationCalls, Is.EqualTo(expected ? 1 : 0));
        }
    }

    [Test]
    public void Combining_rejects_uncovered_required_dependencies([Values] bool withParent)
    {
        FrameDependency covered = Sphincs("covered");
        AggregationInput input = new() { Deps = [covered], Witnesses = [new byte[] { 1 }] };
        RecursiveProofInput? parent = withParent ? new RecursiveProofInput([covered], [2]) : null;

        Assert.Throws<ArgumentException>(() => RecursiveStarkAggregator.Combine([input], [covered, Sphincs("missing")], parent));
    }

    [TestCase(0, true)]
    [TestCase(1, false)]
    public void Empty_recursive_children_require_empty_proofs(int proofLength, bool expected)
    {
        FakeLeanProofVerifier verifier = new(true);
        AggregationInput input = new() { RecursiveProofs = [new RecursiveProofInput([], new byte[proofLength])] };
        bool valid = TryAggregate(input, verifier, Eip8288Verifier.Pinned, out _, out _);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(valid, Is.EqualTo(expected));
            Assert.That(verifier.VerificationCalls, Is.Zero);
        }
    }

    [Test]
    public void Combining_leaves_out_recursive_children_without_required_dependencies([Values] bool withParent)
    {
        FrameDependency required = Sphincs("required");
        RecursiveProofInput unrelated = new([Sphincs("unrelated")], [2]);
        AggregationInput input = new() { Deps = [required], Witnesses = [new byte[] { 1 }], RecursiveProofs = [unrelated] };
        RecursiveProofInput? parent = withParent ? new RecursiveProofInput([required, Sphincs("parent")], [3]) : null;

        AggregationInput combined = RecursiveStarkAggregator.Combine([input], [required], parent);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(combined.RecursiveProofs, Is.EqualTo(withParent ? new[] { parent!.Value } : []));
            Assert.That(combined.Discards, Is.EqualTo(withParent ? new[] { Sphincs("parent") } : []));
        }
    }

    [Test]
    public void Extending_a_parent_discards_its_dependencies_outside_the_required_set()
    {
        FrameDependency kept = Sphincs("kept");
        FrameDependency other = Sphincs("other");
        FrameDependency gone = Sphincs("gone");
        FrameDependency added = Sphincs("added");
        RecursiveProofInput parent = new(Eip8288Dependencies.Canonicalize([kept, other, gone]), [3]);
        AggregationInput witnesses = new() { Deps = [kept, other, added], Witnesses = [new byte[] { 1 }, new byte[] { 1 }, new byte[] { 1 }] };

        AggregationInput extended = RecursiveStarkAggregator.Combine([witnesses], [kept, other, added], parent);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(extended.RecursiveProofs, Is.EqualTo(new[] { parent }));
            Assert.That(extended.Deps, Is.EqualTo(new[] { added }), "the parent covers its retained dependencies");
            Assert.That(extended.Discards, Is.EqualTo(new[] { gone }));
        }
    }

    [Test]
    public void Estimated_cost_counts_a_discarding_call_only_when_the_input_needs_several()
    {
        FrameDependency gone = Sphincs("gone");
        RecursiveProofInput parent = new([Sphincs("kept"), gone], [3]);
        AggregationInput single = new() { RecursiveProofs = [parent], Discards = [gone] };
        AggregationInput several = new() { RecursiveProofs = [parent, new([Sphincs("r1")], [4]), new([Sphincs("r2")], [5])], Discards = [gone] };
        AggregationInput kept = new() { RecursiveProofs = [parent, new([Sphincs("r1")], [4]), new([Sphincs("r2")], [5])] };
        using (Assert.EnterMultipleScope())
        {
            Assert.That(RecursiveStarkAggregator.EstimatedCost(single), Is.EqualTo(RecursiveStarkAggregator.EstimatedCost(new() { RecursiveProofs = [parent, parent] })),
                "a lone discarding parent is one native call");
            Assert.That(RecursiveStarkAggregator.EstimatedCost(several) - RecursiveStarkAggregator.EstimatedCost(kept),
                Is.EqualTo(RecursiveStarkAggregator.EstimatedCost(single)), "a discarding child in a tree needs its own call");
        }
    }

    /// <summary>
    /// Runs the EIP-8288 recursive STARK statement: verify direct dependencies against their witnesses
    /// and each nested proof against its claimed deps, then union and remove discards/duplicates,
    /// returning the filtered set and its commitment.
    /// </summary>
    internal static bool TryAggregate(AggregationInput input, ILeanProofVerifier verifier, in Eip8288Verifier forkVerifier,
        out IReadOnlyList<FrameDependency> filteredDeps, out ValueHash256 depsHash)
    {
        filteredDeps = [];
        depsHash = default;

        if (input.Deps.Count != input.Witnesses.Count) return false;
        // The circuit admits leanSPHINCS only, in direct, recursive and discard lists alike.
        if (!Eip8288Dependencies.AreSchemesEnabled(input.Deps) || !Eip8288Dependencies.AreSchemesEnabled(input.Discards)) return false;
        foreach (RecursiveProofInput recursiveProof in input.RecursiveProofs)
        {
            if (recursiveProof.InnerDeps is null) throw new ArgumentException("Uninitialized recursive proof input", nameof(input));
            if (!Eip8288Dependencies.AreSchemesEnabled(recursiveProof.InnerDeps)) return false;
        }

        List<FrameDependency> allDeps = [];
        for (int i = 0; i < input.Deps.Count; i++)
        {
            FrameDependency dep = input.Deps[i];
            ReadOnlyMemory<byte> witness = input.Witnesses[i];
            if (!verifier.VerifyLeanSphincs(dep.DataHash, dep.VerificationKey, witness.Span)) return false;
            allDeps.Add(dep);
        }

        foreach (RecursiveProofInput recursiveProof in input.RecursiveProofs)
        {
            if (recursiveProof.InnerDeps.Count == 0)
            {
                if (!recursiveProof.Proof.IsEmpty) return false;
                continue;
            }
            // A recursive input above MAX_AGGREGATE_PROOF_BYTES is rejected before its verifier runs.
            if (recursiveProof.Proof.Length is 0 or > Eip8288Constants.MaxAggregateProofBytes) return false;
            ValueHash256 innerHash = Eip8288Dependencies.ComputeDepsHash(recursiveProof.InnerDeps);
            if (!verifier.VerifyRecursiveStark(in innerHash, forkVerifier.AggregatedVk.Bytes, recursiveProof.Proof.Span)) return false;
            allDeps.AddRange(recursiveProof.InnerDeps);
        }

        HashSet<FrameDependency> discards = input.Discards.Count == 0 ? [] : [.. input.Discards];
        // EIP-8288: every discard must occur in the verified input union, matched by its full triple.
        if (discards.Count != 0)
        {
            HashSet<FrameDependency> inputs = [.. allDeps];
            if (!discards.IsSubsetOf(inputs)) return false;
        }

        List<FrameDependency> filtered = [];
        HashSet<FrameDependency> seen = [];
        foreach (FrameDependency dep in allDeps)
        {
            if (!seen.Add(dep)) continue;
            if (discards.Contains(dep)) continue;
            filtered.Add(dep);
        }

        filteredDeps = Eip8288Dependencies.Canonicalize(filtered);
        depsHash = Eip8288Dependencies.ComputeDepsHash(filteredDeps);
        return true;
    }
}
