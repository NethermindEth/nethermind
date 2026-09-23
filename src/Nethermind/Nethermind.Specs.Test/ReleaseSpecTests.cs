// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Specs.Test;

[TestFixture]
public class ReleaseSpecTests
{
    /// <summary>Precompile numbers the 64-bit mask cannot hold, so membership has to reach the set.</summary>
    /// <remarks>0x100 is RIP-7212 and 0x10001/0x10002 are Taiko's, but 0x8000_0000 is the one that pins the
    /// contract: there <see cref="Address.PrecompileIndexOrNegative"/> overflows and reports negative, so
    /// only the address shape can decide, never the sign of the index.</remarks>
    private static readonly long[] NumbersAboveTheMask = [64, 0x100, 0x10001, 0x10002, 0x8000_0000];

    [TestCaseSource(nameof(NumbersAboveTheMask))]
    public void Precompile_above_the_mask_is_found_through_the_set(long number)
    {
        Address registered = Address.FromNumber((UInt256)(ulong)number);
        IReleaseSpec spec = new SpecWithPrecompileAt(registered);

        Assert.That(spec.IsPrecompile(registered), Is.True);
        Assert.That(spec.IsPrecompile(Address.FromNumber((UInt256)(ulong)number + 1)), Is.False);
    }

    /// <summary>Every named fork this assembly defines — mainnet and Gnosis alike.</summary>
    private static IEnumerable<TestCaseData> AllForks() =>
        typeof(NamedReleaseSpec).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(NamedReleaseSpec).IsAssignableFrom(t))
            .Select(t => new TestCaseData((IReleaseSpec)Activator.CreateInstance(t)!).SetArgDisplayNames(t.Name));

    /// <summary>Every registered precompile has to be recognised, at every fork.</summary>
    /// <remarks>Membership answers from the mask below 64, the set above it, and neither for an address
    /// that fails the shape guard, so this sweeps all three branches across every set a fork can build.
    /// The shape invariant itself is enforced where the set is built, which throws — asserting it here too
    /// could not fail, since reading <c>Precompiles</c> would throw first.</remarks>
    [TestCaseSource(nameof(AllForks))]
    public void Every_registered_precompile_is_recognised(IReleaseSpec spec)
    {
        Assert.That(spec.Precompiles, Is.Not.Empty, "a sweep over an empty set would pass without checking anything");

        foreach (AddressAsKey key in spec.Precompiles)
        {
            Address address = key;
            Assert.That(spec.IsPrecompile(address), Is.True, $"{address} is registered but not recognised");
        }
    }

    [Test]
    public void Precompile_membership_could_never_find_is_rejected_when_the_set_is_built()
    {
        // A test covers only the registrations that exist when it is written; this is what catches the
        // rest, turning a silent consensus divergence into a failure on the chain that registered it.
        IReleaseSpec spec = new SpecWithPrecompileAt(Address.FromNumber((UInt256)uint.MaxValue + 1));

        Assert.That(() => spec.Precompiles, Throws.InstanceOf<InvalidOperationException>());
    }

    [Test]
    public void Shape_guard_reaches_the_whole_thirty_two_bit_range()
    {
        // The number lives in the last four bytes, so 0x1_0000_0000 needs a fifth and reads as an ordinary
        // address. That is the ceiling the sweep above defends.
        Assert.That(Address.FromNumber(uint.MaxValue).CouldBePrecompile(), Is.True);
        Assert.That(Address.FromNumber((UInt256)uint.MaxValue + 1).CouldBePrecompile(), Is.False);
    }

    private sealed class SpecWithPrecompileAt(Address address) : ReleaseSpec
    {
        public override FrozenSet<AddressAsKey> BuildPrecompilesCache() => new AddressAsKey[] { address }.ToFrozenSet();
    }
}
