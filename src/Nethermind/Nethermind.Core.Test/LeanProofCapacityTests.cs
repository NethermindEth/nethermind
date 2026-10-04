// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.Core.Test;

public class LeanProofCapacityTests
{
    [Test]
    public void Mixed_output_reserves_one_guest_for_all_claims()
    {
        HashSet<FrameDependency> dependencies = [];
        for (int i = 0; i < Eip8288Constants.MaxProofDependencies; i++)
            dependencies.Add(new(i < Eip8288Constants.MaxGenericStarkProofs
                ? Eip8288Constants.LeanStarkScheme : Eip8288Constants.LeanSphincsScheme,
                ValueKeccak.Compute(i.ToString()), default));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(LeanProofCapacity.CapacityError(dependencies), Is.Null);
            Assert.That(12L + Eip8288Constants.DependencyTripleLength * dependencies.Count
                + Eip8288Constants.MaxMixedGuestProofBytes, Is.EqualTo(Eip8288Constants.MaxProofBytes));
        }
    }

    [Test]
    public void Count_limits_are_shared_with_appendability([Values] bool generic)
    {
        int maximum = generic ? Eip8288Constants.MaxGenericStarkProofs : Eip8288Constants.MaxProofDependencies;
        byte scheme = generic ? Eip8288Constants.LeanStarkScheme : Eip8288Constants.LeanSphincsScheme;
        HashSet<FrameDependency> existing = [];
        for (int i = 0; i < maximum; i++) existing.Add(new(scheme, ValueKeccak.Compute(i.ToString()), default));
        FrameDependency added = new(scheme, ValueKeccak.Compute("extra"), default);
        string expected = generic ? "Generic STARK proof count limit exceeded" : "Dependency proof count limit exceeded";
        LeanProofCapacity.AppendBudget budget = LeanProofCapacity.CreateAppendBudget(existing);
        Assert.That(budget.CapacityError([added]), Is.EqualTo(expected));
        existing.Add(added);
        Assert.That(LeanProofCapacity.CapacityError(existing), Is.EqualTo(expected));
    }

    [Test]
    public void Append_budget_measures_the_block_once()
    {
        FrameDependency generic = Generic(1);
        FrameDependency sphincs = new(Eip8288Constants.LeanSphincsScheme, default, default);
        CountingSet existing = new([generic, sphincs]);
        LeanProofCapacity.AppendBudget budget = LeanProofCapacity.CreateAppendBudget(existing);
        for (int check = 0; check < 100; check++) Assert.That(budget.CapacityError([generic, sphincs]), Is.Null);
        Assert.That(existing.Enumerations, Is.EqualTo(1));
        Assert.That(budget.CapacityError([new(0xff, default, default)]), Is.EqualTo("Unknown dependency proof scheme"));
        Assert.That(existing.Enumerations, Is.EqualTo(1));
    }

    [Test]
    public void Appended_duplicate_claims_are_counted_once()
    {
        FrameDependency generic = Generic(1);
        FrameDependency[] duplicates = new FrameDependency[Eip8288Constants.MaxGenericStarkProofs + 1];
        for (int i = 0; i < duplicates.Length; i++) duplicates[i] = generic;
        Assert.That(LeanProofCapacity.CapacityError(new HashSet<FrameDependency>(), 0, duplicates), Is.Null);
        Assert.That(LeanProofCapacity.CreateAppendBudget(new HashSet<FrameDependency>()).CapacityError(duplicates), Is.Null);
    }

    private sealed class CountingSet(HashSet<FrameDependency> values) : IReadOnlySet<FrameDependency>
    {
        public int Enumerations { get; private set; }
        public int Count => values.Count;
        public bool Contains(FrameDependency item) => values.Contains(item);
        public bool IsProperSubsetOf(IEnumerable<FrameDependency> other) => values.IsProperSubsetOf(other);
        public bool IsProperSupersetOf(IEnumerable<FrameDependency> other) => values.IsProperSupersetOf(other);
        public bool IsSubsetOf(IEnumerable<FrameDependency> other) => values.IsSubsetOf(other);
        public bool IsSupersetOf(IEnumerable<FrameDependency> other) => values.IsSupersetOf(other);
        public bool Overlaps(IEnumerable<FrameDependency> other) => values.Overlaps(other);
        public bool SetEquals(IEnumerable<FrameDependency> other) => values.SetEquals(other);
        public IEnumerator<FrameDependency> GetEnumerator() { Enumerations++; return values.GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static FrameDependency Generic(int value) => new(Eip8288Constants.LeanStarkScheme, ValueKeccak.Compute(value.ToString()), default);
}
