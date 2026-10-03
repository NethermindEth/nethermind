// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Consensus.Eip8288;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Eip8288;

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

        bool ok = RecursiveStarkAggregator.TryAggregate(input, Accepting, out IReadOnlyList<FrameDependency> filtered, out ValueHash256 depsHash);

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

        RecursiveStarkAggregator.TryAggregate(input, Accepting, out IReadOnlyList<FrameDependency> filtered, out _);

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

        RecursiveStarkAggregator.TryAggregate(input, Accepting, out IReadOnlyList<FrameDependency> filtered, out _);

        Assert.That(filtered, Is.EqualTo(Eip8288Dependencies.Canonicalize([a, b])));
    }

    [Test]
    public void Aggregate_fails_on_witness_count_mismatch()
    {
        AggregationInput input = new() { Deps = [Sphincs("a")], Witnesses = [] };

        Assert.That(RecursiveStarkAggregator.TryAggregate(input, Accepting, out _, out _), Is.False);
    }

    [Test]
    public void Aggregate_fails_when_verifier_rejects()
    {
        AggregationInput input = new() { Deps = [Sphincs("a")], Witnesses = [new byte[] { 1 }] };

        Assert.That(RecursiveStarkAggregator.TryAggregate(input, Rejecting, out _, out _), Is.False);
    }

    [Test]
    public void Proof_store_prunes_recursive_dependencies_and_copies_witnesses([Values] bool recursive)
    {
        FrameDependency a = Sphincs("a");
        FrameDependency b = Sphincs("b");
        byte[] first = [1];
        LeanProofStore store = new();
        store.AddVerified([a, b], recursive ? null : [first, [2]], recursive ? first : null);
        first[0] = 9;

        Assert.That(store.TryGetInput([b], out AggregationInput input), Is.True);
        Assert.That(RecursiveStarkAggregator.TryAggregate(input, Accepting, out IReadOnlyList<FrameDependency> deps, out _), Is.True);
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
    public void Proof_store_rejects_missing_dependencies()
    {
        LeanProofStore store = new();
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
        Assert.Throws<OperationCanceledException>(() => RecursiveStarkAggregator.Prove(input, verifier, hash, cancellation.Token));
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

        byte[] proof = RecursiveStarkAggregator.Prove(input, verifier, hash);

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

        RecursiveStarkAggregator.Prove(input, verifier, hash);

        Assert.That(verifier.ProofCalls, Is.EqualTo(1));
    }

    [Test]
    public void Unchanged_parent_cannot_bypass_failed_verification()
    {
        FrameDependency dependency = Sphincs("invalid-parent");
        ValueHash256 hash = Eip8288Dependencies.ComputeDepsHash([dependency]);
        FakeLeanProofVerifier verifier = new(false);
        AggregationInput input = new() { RecursiveProofs = [new([dependency], [2])] };

        Assert.Throws<InvalidOperationException>(() => RecursiveStarkAggregator.Prove(input, verifier, hash));
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

        Assert.Throws<OperationCanceledException>(() => RecursiveStarkAggregator.Prove(input, verifier, hash, cancellation.Token));
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
        byte[] proof = RecursiveStarkAggregator.Prove(new() { RecursiveProofs = recursive }, verifier, in hash);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(proof, Is.EqualTo(new byte[] { 1 }));
            Assert.That(verifier.LargestRecursiveInput, Is.EqualTo(2));
            Assert.That(verifier.ProofCalls, Is.GreaterThan(1));
        }
    }

    [Test]
    public void Hierarchical_children_prune_discarded_generic_claims_before_the_native_count_limit()
    {
        FrameDependency[] dependencies = new FrameDependency[33];
        RecursiveProofInput[] children = new RecursiveProofInput[33];
        for (int i = 0; i < children.Length; i++)
        {
            dependencies[i] = new(Eip8288Constants.LeanStarkScheme, ValueKeccak.Compute(i.ToString()), default);
            children[i] = new([dependencies[i]], LeanProofTestEnvelope.Create([dependencies[i]]));
        }
        GenericBoundedVerifier verifier = new();
        ValueHash256 hash = Eip8288Dependencies.ComputeDepsHash([dependencies[0]]);
        byte[] proof = RecursiveStarkAggregator.Prove(new()
        {
            RecursiveProofs = children,
            Discards = dependencies[1..]
        }, verifier, hash);
        Assert.That(proof, Is.EqualTo(hash.ToByteArray()));
        Assert.That(verifier.ProofCalls, Is.GreaterThan(1));
    }

    [Test]
    public void Longer_duplicate_is_pruned_before_an_intermediate_union_exceeds_output_capacity()
    {
        FrameDependency a = new(Eip8288Constants.LeanStarkScheme, ValueKeccak.Compute("shortest-a"), default);
        FrameDependency b = new(Eip8288Constants.LeanStarkScheme, ValueKeccak.Compute("large-b"), default);
        byte[] longProof = LeanProofTestEnvelope.Create([a], new Dictionary<FrameDependency, int> { [a] = 4 * 1024 * 1024 });
        byte[] otherProof = LeanProofTestEnvelope.Create([b], new Dictionary<FrameDependency, int> { [b] = 5 * 1024 * 1024 });
        byte[] shortProof = LeanProofTestEnvelope.Create([a]);
        EnvelopeBoundedVerifier verifier = new();
        ValueHash256 hash = Eip8288Dependencies.ComputeDepsHash([a, b]);
        byte[] result = RecursiveStarkAggregator.Prove(new()
        {
            RecursiveProofs = [new([a], longProof), new([b], otherProof), new([a], shortProof)]
        }, verifier, hash);
        Dictionary<FrameDependency, int> lengths = [];
        Assert.That(LeanProofCapacity.TryReadGenericWitnessLengths(result, lengths), Is.True);
        Assert.That(lengths[a], Is.EqualTo(1));
        Assert.That(lengths[b], Is.EqualTo(5 * 1024 * 1024));
        Assert.That(verifier.VerifiedProofs, Does.Contain(ValueKeccak.Compute(longProof)), "pruning still authenticates the original parent");
        Assert.That(verifier.VerifiedProofs, Does.Contain(ValueKeccak.Compute(shortProof)), "the selected shortest parent remains in the final statement");
    }

    [Test]
    public void Recursive_generic_source_wins_direct_ties_without_extra_proving([Values(1, 2, 3)] int directWitnessBytes)
    {
        FrameDependency dependency = new(Eip8288Constants.LeanStarkScheme, ValueKeccak.Compute("generic-source-tie"), default);
        byte[] parent = LeanProofTestEnvelope.Create([dependency], new Dictionary<FrameDependency, int> { [dependency] = 2 });
        byte[] directWitness = new byte[directWitnessBytes];
        AggregationInput input = new()
        {
            Deps = [dependency, dependency, dependency, dependency, dependency],
            Witnesses = [directWitness, directWitness, directWitness, directWitness, directWitness],
            RecursiveProofs = [new([dependency], parent)]
        };
        EnvelopeBoundedVerifier verifier = new();
        ValueHash256 hash = Eip8288Dependencies.ComputeDepsHash([dependency]);

        byte[] result = RecursiveStarkAggregator.Prove(input, verifier, hash);

        int directProves = 0;
        int parentPrunes = 0;
        foreach (AggregationInput provingInput in verifier.ProvingInputs)
        {
            if (provingInput.Deps.Count != 0) directProves++;
            if (provingInput.Discards.Count != 0) parentPrunes++;
        }
        Dictionary<FrameDependency, int> lengths = [];
        Assert.That(LeanProofCapacity.TryReadGenericWitnessLengths(result, lengths), Is.True);
        bool shorterDirect = directWitnessBytes < 2;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(lengths[dependency], Is.EqualTo(Math.Min(directWitnessBytes, 2)));
            Assert.That(directProves, Is.EqualTo(shorterDirect ? 1 : 0));
            Assert.That(parentPrunes, Is.EqualTo(shorterDirect ? 1 : 0));
            Assert.That(verifier.DirectStarkVerifications, Is.EqualTo(input.Deps.Count), "omitted direct witnesses still require authentication");
            Assert.That(verifier.VerifiedProofs, Does.Contain(ValueKeccak.Compute(parent)));
        }
    }

    [Test]
    public void Invalid_direct_duplicate_is_verified_before_it_is_omitted([Values] bool tiedRecursiveSource)
    {
        FrameDependency a = new(Eip8288Constants.LeanStarkScheme, ValueKeccak.Compute("invalid-duplicate"), default);
        AggregationInput input = new()
        {
            Deps = tiedRecursiveSource ? [a, a, a, a, a] : [a, a, Sphincs("one"), Sphincs("two"), Sphincs("three")],
            Witnesses = tiedRecursiveSource
                ? [new byte[] { 9, 0 }, new byte[] { 1, 0 }, new byte[] { 1, 0 }, new byte[] { 1, 0 }, new byte[] { 1, 0 }]
                : [new byte[] { 9, 0 }, new byte[] { 1 }, new byte[] { 1 }, new byte[] { 1 }, new byte[] { 1 }],
            RecursiveProofs = tiedRecursiveSource
                ? [new([a], LeanProofTestEnvelope.Create([a], new Dictionary<FrameDependency, int> { [a] = 2 }))]
                : []
        };
        ValueHash256 hash = Eip8288Dependencies.ComputeDepsHash(input.Deps);
        Assert.Throws<InvalidOperationException>(() => RecursiveStarkAggregator.Prove(input, new EnvelopeBoundedVerifier(), hash));
    }

    private sealed class EnvelopeBoundedVerifier : ILeanProofVerifier
    {
        public HashSet<ValueHash256> VerifiedProofs { get; } = [];
        public List<AggregationInput> ProvingInputs { get; } = [];
        public int DirectStarkVerifications { get; private set; }
        public void EnsureAvailable() { }
        public bool VerifyLeanSphincs(in ValueHash256 hash, in ValueHash256 key, ReadOnlySpan<byte> witness) => true;
        public bool VerifyLeanStark(in ValueHash256 hash, in ValueHash256 key, ReadOnlySpan<byte> witness)
        {
            DirectStarkVerifications++;
            return witness.Length > 0 && witness[0] != 9;
        }
        public bool VerifyRecursiveStark(in ValueHash256 hash, ReadOnlySpan<byte> key, ReadOnlySpan<byte> proof)
        {
            VerifiedProofs.Add(ValueKeccak.Compute(proof));
            if (!LeanProofCapacity.TryReadGenericWitnessLengths(proof, new Dictionary<FrameDependency, int>())) return false;
            int count = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(proof[4..]);
            return ValueKeccak.Compute(proof.Slice(8, count * Eip8288Constants.DependencyTripleLength)) == hash;
        }
        public byte[] ProveRecursiveStark(in ValueHash256 hash, ReadOnlySpan<byte> key, AggregationInput input)
        {
            ProvingInputs.Add(input);
            Assert.That(RecursiveStarkAggregator.TryAggregate(input, this, out IReadOnlyList<FrameDependency> dependencies, out ValueHash256 actual), Is.True);
            Assert.That(actual, Is.EqualTo(hash));
            Dictionary<FrameDependency, int> lengths = [];
            foreach (RecursiveProofInput child in input.RecursiveProofs)
                Assert.That(LeanProofCapacity.TryReadGenericWitnessLengths(child.Proof.Span, lengths), Is.True);
            for (int i = 0; i < input.Deps.Count; i++)
                if (input.Deps[i].Scheme == Eip8288Constants.LeanStarkScheme)
                    LeanProofCapacity.AddWitnessLength(lengths, input.Deps[i], input.Witnesses[i].Length);
            Assert.That(LeanProofCapacity.CapacityError(new HashSet<FrameDependency>(dependencies), lengths), Is.Null);
            return LeanProofTestEnvelope.Create(dependencies, lengths);
        }
    }

    private sealed class GenericBoundedVerifier : ILeanProofVerifier
    {
        public int ProofCalls { get; private set; }
        public void EnsureAvailable() { }
        public bool VerifyLeanSphincs(in ValueHash256 hash, in ValueHash256 key, ReadOnlySpan<byte> witness) => true;
        public bool VerifyLeanStark(in ValueHash256 hash, in ValueHash256 key, ReadOnlySpan<byte> witness) => true;
        public bool VerifyRecursiveStark(in ValueHash256 hash, ReadOnlySpan<byte> key, ReadOnlySpan<byte> proof) => true;
        public byte[] ProveRecursiveStark(in ValueHash256 hash, ReadOnlySpan<byte> key, AggregationInput input)
        {
            Assert.That(RecursiveStarkAggregator.TryAggregate(input, Accepting, out IReadOnlyList<FrameDependency> dependencies, out ValueHash256 actual), Is.True);
            Assert.That(actual, Is.EqualTo(hash));
            Assert.That(dependencies.Count, Is.LessThanOrEqualTo(Eip8288Constants.MaxGenericStarkProofs));
            ProofCalls++;
            return hash.ToByteArray();
        }
    }

    [Test]
    public void Proof_store_evicts_oldest_coverage_without_removing_newer_witnesses([Values] bool sameDependency)
    {
        LeanProofStore store = new();
        FrameDependency first = Sphincs("0");
        byte[] witness = new byte[64 * 1024 - Eip8288Constants.DependencyTripleLength];
        for (int i = 0; i < 1025; i++) store.AddVerified([sameDependency ? first : Sphincs(i.ToString())], [witness], null);
        Assert.That(store.TryGetInput([first], out _), Is.EqualTo(sameDependency));
    }

    [Test]
    public void Empty_dependency_proofs_do_not_evict_useful_witnesses()
    {
        LeanProofStore store = new();
        FrameDependency dependency = Sphincs("useful");
        store.AddVerified([dependency], [[1]], null);
        for (int i = 0; i < 1025; i++) store.AddVerified([], null, [2]);
        Assert.That(store.TryGetInput([dependency], out _), Is.True);
    }

    [Test]
    public void Repeated_identical_proofs_do_not_evict_other_coverage()
    {
        LeanProofStore store = new();
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
        LeanProofStore store = new();
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
        LeanProofStore store = new();
        FrameDependency dependency = Sphincs("large");
        byte[] proof = new byte[4 * 1024 * 1024];
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
        Assert.Throws<ArgumentException>(() => RecursiveStarkAggregator.Prove(input, Accepting, default));
        Assert.Throws<ArgumentException>(() => RecursiveStarkAggregator.TryAggregate(input, Accepting, out _, out _));
        Assert.Throws<ArgumentException>(() => RecursiveStarkAggregator.Combine([input], []));
        Assert.Throws<ArgumentException>(() => RecursiveStarkAggregator.InputSize(input));
    }

    [Test]
    public async Task Disposed_admission_scope_revokes_captured_execution_context()
    {
        LeanProofStore store = new();
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
        LeanProofStore store = new();
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
        LeanProofStore store = new();
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
        LeanProofStore store = new();
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
        LeanProofStore store = new();
        List<Transaction> pending = [];
        byte[] rejectedWitness = new byte[4 * 1024 * 1024];
        for (int i = 0; i < 24; i++)
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
        LeanProofStore store = new();
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
        LeanProofStore store = new();
        FrameDependency a = Sphincs("quota-a"), b = Sphincs("quota-b"), c = Sphincs("quota-c");
        byte[] proof = new byte[5 * 1024 * 1024];
        store.AddVerified([a], null, proof);
        store.AddVerified([b], null, proof);
        store.AddVerified([c], null, proof);
        Transaction first = DependencyTransaction(a), shared = DependencyTransaction(a), second = DependencyTransaction(b);
        shared.Hash = Keccak.Compute("shared-transaction");
        Assert.That(store.PinPending(first), Is.True);
        Assert.That(store.PinPending(shared), Is.True, "the same proof is charged once for this sender");
        Assert.That(store.PinPending(second), Is.True);
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

}
