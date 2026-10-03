// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus.Eip8288;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

public class LeanProofBudgetTests
{
    [Test]
    public void Shared_parent_and_repeated_candidates_contribute_witness_bytes_once()
    {
        FrameDependency[] dependencies = new FrameDependency[16];
        for (int i = 0; i < dependencies.Length; i++) dependencies[i] = Dependency(i);
        AggregationInput verified = new() { RecursiveProofs = [new(dependencies, [1])] };
        LeanProofBudget budget = new();
        List<AggregationInput> contributions = [];
        for (int i = 0; i < 1000; i++)
        {
            FrameDependency[] required = [dependencies[i % dependencies.Length]];
            Assert.That(budget.TryUseInclusionList(verified, budget.Missing(required), out AggregationInput candidate), Is.True);
            Assert.That(budget.TryPrepare(candidate, required, out AggregationInput contribution, out string? error), Is.True, error);
            budget.Commit(contribution, required);
            Assert.That(budget.Missing(dependencies), Is.Empty, "the retained parent already proves later candidates");
            if (contribution.RecursiveProofs.Count != 0) contributions.Add(contribution);
        }
        AggregationInput combined = RecursiveStarkAggregator.Combine(contributions, dependencies);
        Assert.That(contributions, Has.Count.EqualTo(1));
        Assert.That(combined.RecursiveProofs, Has.Count.EqualTo(1));
        Assert.That(combined.Discards, Is.Empty);
        Assert.That(budget.TryUseInclusionList(verified, [Dependency(99)], out _), Is.False);
    }

    [Test]
    public void Witness_budget_accepts_the_boundary_and_rejects_only_the_next_new_dependency()
    {
        LeanProofBudget budget = new();
        FrameDependency first = Dependency(1), second = Dependency(2);
        byte[] witness = new byte[RecursiveStarkAggregator.MaxProductionWitnessBytes - 12 - Eip8288Constants.DependencyTripleLength - 4];
        AggregationInput input = new() { Deps = [first], Witnesses = [witness] };
        Assert.That(budget.TryPrepare(input, [first], out AggregationInput contribution, out _), Is.True);
        budget.Commit(contribution, [first]);
        Assert.That(budget.TryPrepare(input, [first], out AggregationInput repeat, out _), Is.True);
        Assert.That(repeat.Deps, Is.Empty);
        Assert.That(budget.TryPrepare(new() { Deps = [second], Witnesses = [new byte[] { 1 }] }, [second], out _, out string? error), Is.False);
        Assert.That(error, Is.EqualTo("Dependency witness budget exceeded"));
    }

    [Test]
    public void Uncovered_new_dependencies_are_rejected_before_budget_accounting()
    {
        LeanProofBudget budget = new();
        FrameDependency dependency = Dependency(1);
        Assert.That(budget.TryPrepare(new(), [dependency], out _, out string? error), Is.False);
        Assert.That(error, Is.EqualTo("Missing verified dependency witnesses"));
        Assert.That(budget.Missing([dependency]), Has.Count.EqualTo(1));
    }

    [Test]
    public void Parent_coverage_bound_remains_enforced_before_execution()
    {
        LeanProofBudget budget = new();
        FrameDependency[] dependencies = new FrameDependency[Eip8288Constants.MaxProofDependencies];
        for (int i = 0; i < dependencies.Length; i++) dependencies[i] = Dependency(i);
        Assert.That(budget.TryPrepare(new() { RecursiveProofs = [new(dependencies, [1])] }, [dependencies[0]],
            out AggregationInput contribution, out _), Is.True);
        budget.Commit(contribution, [dependencies[0]]);
        FrameDependency extra = Dependency(dependencies.Length);
        Assert.That(budget.TryPrepare(new() { Deps = [extra], Witnesses = [new byte[] { 1 }] }, [extra], out _, out string? error), Is.False);
        Assert.That(error, Is.EqualTo("Dependency witness coverage limit exceeded"));
    }

    private static FrameDependency Dependency(int index) => new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute(index.ToString()), default);
}
