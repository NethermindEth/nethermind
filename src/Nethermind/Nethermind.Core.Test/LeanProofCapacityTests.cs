// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
            Assert.That(LeanProofCapacity.CapacityError(dependencies, batchPricing: false), Is.Null);
            // EIP-8288: stark_proof carries no dependencies, so the guest proof is the whole proof; the native acceptance bound
            // covers the local aggregate proof bound, which callers enforce first.
            Assert.That(Eip8288Constants.MaxProofBytes, Is.GreaterThanOrEqualTo(Eip8288Constants.MaxAggregateProofBytes));
        }
    }

    [Test]
    public void Count_limits_are_shared_with_appendability([Values] bool generic, [Values] bool batchPricing)
    {
        // The batch-pricing guest has no leanSTARK bound of its own, only the list bound.
        int maximum = generic && !batchPricing ? Eip8288Constants.MaxGenericStarkProofs : Eip8288Constants.MaxProofDependencies;
        byte scheme = generic ? Eip8288Constants.LeanStarkScheme : Eip8288Constants.LeanSphincsScheme;
        HashSet<FrameDependency> existing = [];
        for (int i = 0; i < maximum; i++) existing.Add(new(scheme, ValueKeccak.Compute(i.ToString()), default));
        FrameDependency added = new(scheme, ValueKeccak.Compute("extra"), default);
        string expected = generic && !batchPricing ? "Generic STARK proof count limit exceeded" : "Dependency proof count limit exceeded";
        Assert.That(LeanProofCapacity.CapacityError(existing, generic ? maximum : 0, [added], batchPricing), Is.EqualTo(expected));
        existing.Add(added);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(LeanProofCapacity.CapacityError(existing, batchPricing), Is.EqualTo(expected));
            // ethereum/EIPs#12664: block building refuses a statement here, so it never nears MAX_DEPS_PER_BLOCK.
            Assert.That(existing, Has.Count.LessThan(Eip8288Constants.MaxDepsPerBlock));
        }
    }

    [Test]
    public void Appended_duplicate_claims_are_counted_once()
    {
        FrameDependency generic = Generic(1);
        FrameDependency[] duplicates = new FrameDependency[Eip8288Constants.MaxGenericStarkProofs + 1];
        for (int i = 0; i < duplicates.Length; i++) duplicates[i] = generic;
        Assert.That(LeanProofCapacity.CapacityError(new HashSet<FrameDependency>(), 0, duplicates, batchPricing: false), Is.Null);
    }

    [TestCase((byte)0x00)]
    [TestCase((byte)0x12)]
    public void Schemes_outside_enabled_schemes_have_no_capacity(byte scheme)
    {
        FrameDependency disabled = new(scheme, ValueKeccak.Compute("disabled"), default);
        Assert.That(LeanProofCapacity.CapacityError(new HashSet<FrameDependency>(), 0, [disabled], batchPricing: false),
            Is.EqualTo("Unknown dependency proof scheme"));
    }

    [Test]
    public void Local_proof_bounds_are_enforced([Values] bool batchPricing)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(LeanProofCapacity.CapacityError(Distinct(Eip8288Constants.MaxProofDependencies + 1, 0), batchPricing), Is.Not.Null);
            Assert.That(LeanProofCapacity.CapacityError(Distinct(Eip8288Constants.MaxGenericStarkProofs + 1,
                Eip8288Constants.MaxGenericStarkProofs + 1), batchPricing), batchPricing ? Is.Null : Is.Not.Null);
        }
    }

    // Without batch pricing a node keeps EIP-8288's guest unchanged: the base branch's key and its nlean_limits.
    [Test]
    public void Flag_off_key_and_limits_equal_eip8288s()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Convert.ToHexStringLower(Eip8288Constants.AggregatedVk), Is.EqualTo("f6f37690cf7241c9ea24392f349867acc1c8e06034e8ce149e2f11a25eec56f9"));
            Assert.That(Nethermind.Specs.Forks.Eip8288Prototype.Instance.IsEipBatchedDependencyPricingEnabled, Is.False);
            Assert.That(Eip8288Verifier.For(Nethermind.Specs.Forks.Eip8288Prototype.Instance).AggregatedVk, Is.EqualTo(Eip8288Verifier.PinnedAggregatedVk));
            Assert.That(new[] { Eip8288Constants.MaxProofBytes, Eip8288Constants.MaxProofDependencies, Eip8288Constants.BaseMaxRecursiveInputs,
                    Eip8288Constants.LeanSphincsWitnessBytes, Eip8288Constants.MaxAggregationInputBytes, Eip8288Constants.MaxGenericStarkProofs,
                    Eip8288Constants.MaxLeanStarkInstructions },
                Is.EqualTo(new[] { 8 * 1024 * 1024, 256, 16, 32 + 6176, 18 * 1024 * 1024, 16, 2048 }));
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
