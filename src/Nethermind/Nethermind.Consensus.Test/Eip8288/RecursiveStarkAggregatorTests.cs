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
        Assert.That(filtered, Is.EqualTo(new[] { a, b }));
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

        Assert.That(filtered, Is.EqualTo(new[] { a, b }));
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
    public void Aggregate_with_placeholder_verifier_accepts_valid_witness_and_rejects_wrong_one()
    {
        FrameDependency a = Sphincs("a");
        byte[] witness = PlaceholderLeanProofVerifier.ProveLeanSphincs(a.DataHash, a.VerificationKey);

        AggregationInput valid = new() { Deps = [a], Witnesses = [witness] };
        Assert.That(RecursiveStarkAggregator.TryAggregate(valid, PlaceholderLeanProofVerifier.Instance, out _, out _), Is.True);

        AggregationInput wrong = new() { Deps = [a], Witnesses = [[9]] };
        Assert.That(RecursiveStarkAggregator.TryAggregate(wrong, PlaceholderLeanProofVerifier.Instance, out _, out _), Is.False);
    }
}
