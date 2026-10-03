// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus.Eip8288;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
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
    public void Large_verified_parent_is_not_rejected_by_a_private_batching_target()
    {
        LeanProofBudget budget = new();
        FrameDependency first = new(Eip8288Constants.LeanStarkScheme, ValueKeccak.Compute("generic"), default), second = Dependency(2);
        byte[] proof = LeanProofTestEnvelope.Create([first, second], new Dictionary<FrameDependency, int> { [first] = 5 * 1024 * 1024 });
        AggregationInput input = new() { RecursiveProofs = [new(new FrameDependency[] { first, second }, proof)] };
        Assert.That(budget.TryPrepare(input, [first], out AggregationInput contribution, out _), Is.True);
        budget.Commit(contribution, [first]);
        Assert.That(budget.TryPrepare(input, [second], out AggregationInput repeat, out _), Is.True);
        Assert.That(repeat.RecursiveProofs, Is.Empty);

    }

    [Test]
    public void Disjoint_carried_generic_witnesses_obey_the_public_output_bound()
    {
        LeanProofBudget budget = new();
        FrameDependency first = new(Eip8288Constants.LeanStarkScheme, ValueKeccak.Compute("first"), default);
        FrameDependency second = new(Eip8288Constants.LeanStarkScheme, ValueKeccak.Compute("second"), default);
        static AggregationInput Input(FrameDependency dependency) => new()
        {
            RecursiveProofs = [new([dependency], LeanProofTestEnvelope.Create([dependency],
                new Dictionary<FrameDependency, int> { [dependency] = 4 * 1024 * 1024 }))]
        };
        Assert.That(budget.TryPrepare(Input(first), [first], out AggregationInput accepted, out _), Is.True);
        budget.Commit(accepted, [first]);
        Assert.That(budget.TryPrepare(Input(second), [second], out _, out string? error), Is.False);
        Assert.That(error, Is.EqualTo("Dependency proof output limit exceeded"));
        Assert.That(budget.Missing([second]), Has.Count.EqualTo(1));
    }

    [Test]
    public void Public_inclusion_proof_replaces_a_larger_witness_for_an_already_covered_claim()
    {
        FrameDependency first = new(Eip8288Constants.LeanStarkScheme, ValueKeccak.Compute("first"), default);
        FrameDependency second = new(Eip8288Constants.LeanStarkScheme, ValueKeccak.Compute("second"), default);
        FrameDependency sphincs = Dependency(3);
        LeanProofBudget budget = new();
        AggregationInput privateParent = new()
        {
            RecursiveProofs = [new([first, second], LeanProofTestEnvelope.Create([first, second],
                new Dictionary<FrameDependency, int> { [first] = 4 * 1024 * 1024, [second] = 3 * 1024 * 1024 }))]
        };
        Assert.That(budget.TryPrepare(privateParent, [second], out AggregationInput previous, out _), Is.True);
        budget.Commit(previous, [second]);
        Assert.That(budget.TryPrepare(new() { Deps = [sphincs], Witnesses = [new byte[] { 1 }] }, [first, sphincs], out _, out _), Is.False);
        AggregationInput publicParent = new() { RecursiveProofs = [new([first, sphincs], LeanProofTestEnvelope.Create([first, sphincs]))] };
        Assert.That(budget.TryUseInclusionList(publicParent, [first, sphincs], out AggregationInput candidate), Is.True);
        Assert.That(budget.TryPrepare(candidate, [first, sphincs], out AggregationInput contribution, out string? error), Is.True, error);
        Assert.That(contribution.RecursiveProofs, Has.Count.EqualTo(1), "the shorter public proof must reach the native fold");
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
    public void Unneeded_parent_claims_do_not_limit_the_selected_union()
    {
        LeanProofBudget budget = new();
        FrameDependency[] dependencies = new FrameDependency[Eip8288Constants.MaxProofDependencies];
        for (int i = 0; i < dependencies.Length; i++) dependencies[i] = Dependency(i);
        Assert.That(budget.TryPrepare(new() { RecursiveProofs = [new(dependencies, [1])] }, [dependencies[0]],
            out AggregationInput contribution, out _), Is.True);
        budget.Commit(contribution, [dependencies[0]]);
        FrameDependency extra = Dependency(dependencies.Length);
        Assert.That(budget.TryPrepare(new() { Deps = [extra], Witnesses = [new byte[] { 1 }] }, [extra], out _, out _), Is.True);
    }

    [Test]
    public void Generic_union_limit_rejects_a_new_claim_but_preserves_existing_coverage()
    {
        LeanProofBudget budget = new();
        FrameDependency[] dependencies = new FrameDependency[Eip8288Constants.MaxGenericStarkProofs];
        for (int i = 0; i < dependencies.Length; i++)
            dependencies[i] = new(Eip8288Constants.LeanStarkScheme, ValueKeccak.Compute(i.ToString()), default);
        AggregationInput parent = new() { RecursiveProofs = [new(dependencies, LeanProofTestEnvelope.Create(dependencies))] };
        Assert.That(budget.TryPrepare(parent, dependencies, out AggregationInput contribution, out _), Is.True);
        budget.Commit(contribution, dependencies);
        Assert.That(budget.TryPrepare(new(), [dependencies[0]], out _, out _), Is.True);
        FrameDependency extra = new(Eip8288Constants.LeanStarkScheme, ValueKeccak.Compute("extra"), default);
        Assert.That(budget.TryPrepare(new() { Deps = [extra], Witnesses = [new byte[] { 1 }] }, [extra], out _, out string? error), Is.False);
        Assert.That(error, Is.EqualTo("Generic STARK proof count limit exceeded"));
    }

    private static FrameDependency Dependency(int index) => new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute(index.ToString()), default);
}
