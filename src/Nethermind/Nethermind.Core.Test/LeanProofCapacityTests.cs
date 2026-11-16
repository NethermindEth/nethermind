// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.Core.Test;

public class LeanProofCapacityTests
{
    [Test]
    public void Mixed_output_reserves_one_guest_for_all_claims()
    {
        HashSet<FrameDependency> dependencies = Distinct(Eip8288Constants.MaxProofDependencies, Eip8288Constants.MaxLeanStarkWitnessesPerStep + 1);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(LeanProofCapacity.CapacityError(dependencies), Is.Null);
            // EIP-8288: stark_proof carries no dependencies, so the guest proof is the whole proof; the native acceptance bound
            // covers the local aggregate proof bound, which callers enforce first.
            Assert.That(Eip8288Constants.MaxProofBytes, Is.GreaterThanOrEqualTo(Eip8288Constants.MaxAggregateProofBytes));
        }
    }

    [Test]
    public void Count_limits_are_shared_with_appendability([Values] bool generic)
    {
        byte scheme = generic ? Eip8288Constants.LeanStarkScheme : Eip8288Constants.LeanSphincsScheme;
        HashSet<FrameDependency> existing = [];
        for (int i = 0; i < Eip8288Constants.MaxProofDependencies; i++) existing.Add(new(scheme, ValueKeccak.Compute(i.ToString()), default));
        FrameDependency added = new(scheme, ValueKeccak.Compute("extra"), default);
        const string expected = "Dependency proof count limit exceeded";
        Assert.That(LeanProofCapacity.CapacityError(existing, [added]), Is.EqualTo(expected));
        existing.Add(added);
        Assert.That(LeanProofCapacity.CapacityError(existing), Is.EqualTo(expected));
    }

    [Test]
    public void Appended_duplicate_claims_are_counted_once()
    {
        HashSet<FrameDependency> existing = Distinct(Eip8288Constants.MaxProofDependencies - 1, 0);
        FrameDependency generic = Generic(-1);
        Assert.That(LeanProofCapacity.CapacityError(existing, [generic, generic]), Is.Null);
    }

    [TestCase((byte)0x00)]
    [TestCase((byte)0x12)]
    public void Schemes_outside_enabled_schemes_have_no_capacity(byte scheme)
    {
        FrameDependency disabled = new(scheme, ValueKeccak.Compute("disabled"), default);
        Assert.That(LeanProofCapacity.CapacityError(new HashSet<FrameDependency>(), [disabled]),
            Is.EqualTo("Unknown dependency proof scheme"));
    }

    private static HashSet<FrameDependency> Distinct(int count, int leanStark)
    {
        HashSet<FrameDependency> dependencies = [];
        for (int i = 0; i < count; i++) dependencies.Add(i < leanStark ? Generic(i) : Sphincs(i));
        return dependencies;
    }

    private static FrameDependency Sphincs(int value) => new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute(value.ToString()), default);

    private static FrameDependency Generic(int value) => new(Eip8288Constants.LeanStarkScheme, ValueKeccak.Compute(value.ToString()), default);
}
