// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus.ProofAggregation;
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
    public void Compressed_parents_share_one_output_reservation()
    {
        LeanProofBudget budget = new();
        FrameDependency first = new(Eip8288Constants.LeanStarkScheme, ValueKeccak.Compute("first"), default);
        FrameDependency second = new(Eip8288Constants.LeanStarkScheme, ValueKeccak.Compute("second"), default);
        static AggregationInput Input(FrameDependency dependency) => new()
        {
            RecursiveProofs = [new([dependency], LeanProofTestEnvelope.Create([dependency],
                Eip8288Constants.MaxMixedGuestProofBytes))]
        };
        AggregationInput firstInput = Input(first), secondInput = Input(second);
        Assert.That(RecursiveStarkAggregator.InputSize(new()
        {
            RecursiveProofs = [firstInput.RecursiveProofs[0], secondInput.RecursiveProofs[0]]
        }), Is.GreaterThan(RecursiveStarkAggregator.MaxProductionWitnessBytes));
        Assert.That(budget.TryPrepare(firstInput, [first], out AggregationInput accepted, out _), Is.True);
        budget.Commit(accepted, [first]);
        Assert.That(budget.TryPrepare(secondInput, [second], out AggregationInput appended, out string? error), Is.True, error);
        budget.Commit(appended, [second]);
        Assert.That(budget.Missing([first, second]), Is.Empty);
        Assert.That(budget.TryPrepare(secondInput, [second], out AggregationInput repeat, out _), Is.True);
        Assert.That(repeat.RecursiveProofs, Is.Empty);
    }

    [Test]
    public void Public_inclusion_proof_can_supply_claims_beyond_private_coverage()
    {
        FrameDependency first = new(Eip8288Constants.LeanStarkScheme, ValueKeccak.Compute("first"), default);
        FrameDependency second = new(Eip8288Constants.LeanStarkScheme, ValueKeccak.Compute("second"), default);
        FrameDependency sphincs = Dependency(3);
        LeanProofBudget budget = new();
        AggregationInput privateParent = new() { RecursiveProofs = [new([first, second], LeanProofTestEnvelope.Create([first, second]))] };
        Assert.That(budget.TryPrepare(privateParent, [second], out AggregationInput previous, out _), Is.True);
        budget.Commit(previous, [second]);
        Assert.That(budget.TryPrepare(new(), [first, sphincs], out _, out _), Is.False);
        AggregationInput publicParent = new() { RecursiveProofs = [new([first, sphincs], LeanProofTestEnvelope.Create([first, sphincs]))] };
        Assert.That(budget.TryUseInclusionList(publicParent, [first, sphincs], out AggregationInput candidate), Is.True);
        Assert.That(budget.TryPrepare(candidate, [first, sphincs], out AggregationInput contribution, out string? error), Is.True, error);
        Assert.That(contribution.RecursiveProofs, Has.Count.EqualTo(1));
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
    public void Proof_union_limit_rejects_a_new_claim_but_preserves_existing_coverage([Values] bool generic)
    {
        LeanProofBudget budget = new();
        int maximum = generic ? Eip8288Constants.MaxGenericStarkProofs : Eip8288Constants.MaxProofDependencies;
        byte scheme = generic ? Eip8288Constants.LeanStarkScheme : Eip8288Constants.LeanSphincsScheme;
        FrameDependency[] dependencies = new FrameDependency[maximum];
        for (int i = 0; i < dependencies.Length; i++)
            dependencies[i] = new(scheme, ValueKeccak.Compute(i.ToString()), default);
        AggregationInput parent = new() { RecursiveProofs = [new(dependencies, LeanProofTestEnvelope.Create(dependencies))] };
        Assert.That(budget.TryPrepare(parent, dependencies, out AggregationInput contribution, out _), Is.True);
        budget.Commit(contribution, dependencies);
        Assert.That(budget.TryPrepare(new(), [dependencies[0]], out _, out _), Is.True);
        FrameDependency extra = new(scheme, ValueKeccak.Compute("extra"), default);
        Assert.That(budget.TryPrepare(new() { Deps = [extra], Witnesses = [new byte[] { 1 }] }, [extra], out _, out string? error), Is.False);
        Assert.That(error, Is.EqualTo(generic ? "Generic STARK proof count limit exceeded" : "Dependency proof count limit exceeded"));
    }

    private static FrameDependency Dependency(int index) => new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute(index.ToString()), default);
}
