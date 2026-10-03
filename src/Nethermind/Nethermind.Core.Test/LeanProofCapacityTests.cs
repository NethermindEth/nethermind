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
    public void Two_valid_envelopes_can_exceed_the_public_union_output_bound()
    {
        FrameDependency first = Generic(1), second = Generic(2);
        Dictionary<FrameDependency, int> lengths = [];
        foreach (FrameDependency dependency in new[] { first, second })
            Assert.That(LeanProofCapacity.TryReadGenericWitnessLengths(
                LeanProofTestEnvelope.Create([dependency], new Dictionary<FrameDependency, int> { [dependency] = 4 * 1024 * 1024 }), lengths), Is.True);
        Assert.That(LeanProofCapacity.CapacityError(new HashSet<FrameDependency> { first, second }, lengths),
            Is.EqualTo("Dependency proof output limit exceeded"));
    }

    [Test]
    public void Shorter_verified_alternative_restores_output_capacity()
    {
        FrameDependency first = Generic(1), second = Generic(2);
        Dictionary<FrameDependency, int> lengths = new() { [first] = 4 * 1024 * 1024, [second] = 4 * 1024 * 1024 };
        Assert.That(LeanProofCapacity.TryReadGenericWitnessLengths(LeanProofTestEnvelope.Create([first]), lengths), Is.True);
        Assert.That(lengths[first], Is.EqualTo(1));
        Assert.That(LeanProofCapacity.CapacityError(new HashSet<FrameDependency> { first, second }, lengths), Is.Null);
    }

    [Test]
    public void Sphincs_reservation_is_shared_with_the_generic_output_bound()
    {
        FrameDependency generic = Generic(1);
        FrameDependency sphincs = new(Eip8288Constants.LeanSphincsScheme, default, default);
        Dictionary<FrameDependency, int> lengths = new() { [generic] = 7 * 1024 * 1024 };
        Assert.That(LeanProofCapacity.CapacityError(new HashSet<FrameDependency> { generic }, lengths), Is.Null);
        Assert.That(LeanProofCapacity.CapacityError(new HashSet<FrameDependency> { generic, sphincs }, lengths),
            Is.EqualTo("Dependency proof output limit exceeded"));
    }

    [Test]
    public void Malformed_envelope_does_not_publish_partial_lengths([Values] bool truncate)
    {
        byte[] proof = LeanProofTestEnvelope.Create([Generic(1)]);
        if (truncate) proof = proof[..^1];
        else proof[4] = 255;
        Dictionary<FrameDependency, int> lengths = [];
        Assert.That(LeanProofCapacity.TryReadGenericWitnessLengths(proof, lengths), Is.False);
        Assert.That(lengths, Is.Empty);
    }

    [Test]
    public void Append_budget_measures_the_block_once_and_preserves_full_union_limits()
    {
        FrameDependency generic = Generic(1);
        FrameDependency sphincs = new(Eip8288Constants.LeanSphincsScheme, default, default);
        CountingSet existing = new([generic, sphincs]);
        Dictionary<FrameDependency, int> lengths = new()
        {
            [generic] = Eip8288Constants.MaxProofBytes - Eip8288Constants.MaxSphincsGuestProofBytes
                - 16 - 3 * Eip8288Constants.DependencyTripleLength - 4
        };
        LeanProofCapacity.AppendBudget budget = LeanProofCapacity.CreateAppendBudget(existing, lengths);
        for (int check = 0; check < 100; check++) Assert.That(budget.CapacityError([generic, sphincs]), Is.Null);
        Assert.That(existing.Enumerations, Is.EqualTo(1));
        FrameDependency extra = new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute("extra"), default);
        Assert.That(budget.CapacityError([extra]), Is.EqualTo("Dependency proof output limit exceeded"));
        Assert.That(LeanProofCapacity.CapacityError(new HashSet<FrameDependency> { generic, sphincs, extra }, lengths),
            Is.EqualTo(budget.CapacityError([extra])));
        Assert.That(existing.Enumerations, Is.EqualTo(1));
    }

    [Test]
    public void Appended_duplicate_claims_are_counted_once()
    {
        FrameDependency generic = Generic(1);
        FrameDependency[] duplicates = new FrameDependency[Eip8288Constants.MaxGenericStarkProofs + 1];
        for (int i = 0; i < duplicates.Length; i++) duplicates[i] = generic;
        Assert.That(LeanProofCapacity.CapacityError(new HashSet<FrameDependency>(), 0, duplicates), Is.Null);
        LeanProofCapacity.AppendBudget budget = LeanProofCapacity.CreateAppendBudget(new HashSet<FrameDependency>(),
            new Dictionary<FrameDependency, int> { [generic] = 1 });
        Assert.That(budget.CapacityError(duplicates), Is.Null);
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
