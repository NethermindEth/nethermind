// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Consensus.Eip8288;
using Nethermind.Core;
using Nethermind.Core.Crypto;
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
            Assert.That(verifier.LargestRecursiveInput, Is.EqualTo(16));
            Assert.That(verifier.ProofCalls, Is.EqualTo(4));
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
            children[i] = new([dependencies[i]], [1]);
        }
        GenericBoundedVerifier verifier = new();
        ValueHash256 hash = Eip8288Dependencies.ComputeDepsHash([dependencies[0]]);
        byte[] proof = RecursiveStarkAggregator.Prove(new()
        {
            RecursiveProofs = children,
            Discards = dependencies[1..]
        }, verifier, hash);
        Assert.That(proof, Is.EqualTo(hash.ToByteArray()));
        Assert.That(verifier.ProofCalls, Is.EqualTo(4));
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
        for (int i = 0; i < 1025; i++) store.AddVerified([sameDependency ? first : Sphincs(i.ToString())], [[1]], null);
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
        using (store.BeginAdmission([dependency]))
        {
            Assert.That(store.Covers(transaction), Is.True);
            captured = Task.Run(async () => { await release.Task; return store.Covers(transaction); });
        }
        release.SetResult();
        Assert.That(await captured, Is.False);
        Assert.That(store.Covers(transaction), Is.False);
    }

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
