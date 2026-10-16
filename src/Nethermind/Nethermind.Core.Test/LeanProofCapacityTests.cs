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
        HashSet<FrameDependency> dependencies = [];
        for (int i = 0; i < Eip8288Constants.MaxProofDependencies; i++)
            dependencies.Add(new(i < Eip8288Constants.MaxGenericStarkProofs
                ? Eip8288Constants.LeanStarkScheme : Eip8288Constants.LeanSphincsScheme,
                ValueKeccak.Compute(i.ToString()), default));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(LeanProofCapacity.CapacityError(dependencies), Is.Null);
            // EIP-8288: stark_proof carries no dependencies, so the guest proof is the whole proof; the native acceptance bound
            // covers MAX_AGGREGATE_PROOF_BYTES, which callers enforce first.
            Assert.That(Eip8288Constants.MaxMixedGuestProofBytes, Is.GreaterThanOrEqualTo(Eip8288Constants.MaxAggregateProofBytes));
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
        Assert.That(LeanProofCapacity.CapacityError(existing, generic ? maximum : 0, [added]), Is.EqualTo(expected));
        existing.Add(added);
        Assert.That(LeanProofCapacity.CapacityError(existing), Is.EqualTo(expected));
    }

    [Test]
    public void Appended_duplicate_claims_are_counted_once()
    {
        FrameDependency generic = Generic(1);
        FrameDependency[] duplicates = new FrameDependency[Eip8288Constants.MaxGenericStarkProofs + 1];
        for (int i = 0; i < duplicates.Length; i++) duplicates[i] = generic;
        Assert.That(LeanProofCapacity.CapacityError(new HashSet<FrameDependency>(), 0, duplicates), Is.Null);
    }

    [Test]
    public void Schemes_outside_enabled_schemes_have_no_capacity()
    {
        FrameDependency disabled = new(0x12, ValueKeccak.Compute("disabled"), default);
        Assert.That(LeanProofCapacity.CapacityError(new HashSet<FrameDependency>(), 0, [disabled]),
            Is.EqualTo("Unknown dependency proof scheme"));
    }

    [Test]
    public void Local_proof_envelope_is_below_the_aggregate_wrapper_ceiling()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Eip8288Constants.MaxProofDependencies, Is.LessThan(Eip8288Constants.MaxDepsPerAggregateWrapper));
            Assert.That(LeanProofCapacity.CapacityError(Distinct(Eip8288Constants.MaxProofDependencies + 1, 0)), Is.Not.Null);
            Assert.That(LeanProofCapacity.CapacityError(Distinct(Eip8288Constants.MaxGenericStarkProofs + 1,
                Eip8288Constants.MaxGenericStarkProofs + 1)), Is.Not.Null);
        }
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
