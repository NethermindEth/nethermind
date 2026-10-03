// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

    private static FrameDependency Generic(int value) => new(Eip8288Constants.LeanStarkScheme, ValueKeccak.Compute(value.ToString()), default);
}
