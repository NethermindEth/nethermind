// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus.Eip8288;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
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
            Witnesses = [[1], [1], [1]],
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
            Witnesses = [[1], [1]],
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
            Witnesses = [[1]],
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
        AggregationInput input = new() { Deps = [Sphincs("a")], Witnesses = [[1]] };

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
            Assert.That(input.RecursiveProofs[0].Proof[0], Is.EqualTo(1));
        }
        else
        {
            Assert.That(input.Deps, Is.EqualTo(new[] { b }));
            Assert.That(input.Witnesses[0], Is.EqualTo(new byte[] { 2 }));
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
    public void Combining_shared_dependencies_retains_one_witness_and_one_identical_parent()
    {
        FrameDependency a = Sphincs("a");
        FrameDependency b = Sphincs("b");
        AggregationInput input = new() { Deps = [a], Witnesses = [[1]], RecursiveProofs = [new([b], [2])] };
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
