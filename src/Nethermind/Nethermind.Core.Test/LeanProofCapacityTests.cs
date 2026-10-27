// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.Core.Test;

public class LeanProofCapacityTests
{
    [Test]
    public void One_guest_proof_covers_every_claim_up_to_the_pinned_bound()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(LeanProofCapacity.CapacityError(Distinct(Eip8288Constants.MaxProofDependencies)), Is.Null);
            // EIP-8288: stark_proof carries no dependencies, so the guest proof is the whole proof; the native acceptance bound
            // covers the local aggregate proof bound, which callers enforce first.
            Assert.That(Eip8288Constants.MaxMixedGuestProofBytes, Is.GreaterThanOrEqualTo(Eip8288Constants.MaxAggregateProofBytes));
        }
    }

    [Test]
    public void Count_limit_is_shared_with_appendability()
    {
        HashSet<FrameDependency> existing = Distinct(Eip8288Constants.MaxProofDependencies);
        FrameDependency added = new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute("extra"), default);
        const string expected = "Dependency proof count limit exceeded";
        Assert.That(LeanProofCapacity.CapacityError(existing, [added]), Is.EqualTo(expected));
        existing.Add(added);
        Assert.That(LeanProofCapacity.CapacityError(existing), Is.EqualTo(expected));
    }

    [Test]
    public void Appended_duplicate_claims_are_counted_once()
    {
        HashSet<FrameDependency> existing = Distinct(Eip8288Constants.MaxProofDependencies - 1);
        FrameDependency added = Sphincs(-1);
        Assert.That(LeanProofCapacity.CapacityError(existing, [added, added, added]), Is.Null);
    }

    [TestCase(Eip8288Constants.LeanStarkScheme)]
    [TestCase((byte)0x12)]
    public void Schemes_other_than_leansphincs_have_no_capacity(byte scheme)
    {
        FrameDependency disabled = new(scheme, ValueKeccak.Compute("disabled"), default);
        Assert.That(LeanProofCapacity.CapacityError(new HashSet<FrameDependency>(), [disabled]),
            Is.EqualTo("Unknown dependency proof scheme"));
    }

    [Test]
    public void Local_proof_envelope_is_below_the_aggregate_wrapper_ceiling()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Eip8288Constants.MaxProofDependencies, Is.LessThan(Eip8288Constants.MaxDepsPerAggregateWrapper));
            Assert.That(LeanProofCapacity.CapacityError(Distinct(Eip8288Constants.MaxProofDependencies + 1)), Is.Not.Null);
        }
    }

    private static HashSet<FrameDependency> Distinct(int count)
    {
        HashSet<FrameDependency> dependencies = [];
        for (int i = 0; i < count; i++) dependencies.Add(Sphincs(i));
        return dependencies;
    }

    private static FrameDependency Sphincs(int value) => new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute(value.ToString()), default);
}
