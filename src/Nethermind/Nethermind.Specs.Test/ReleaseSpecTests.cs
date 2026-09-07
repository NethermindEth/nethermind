// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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

    /// <summary>Bools on the interface that are not fork flags, so <see cref="EveryForkFlag"/> skips them.</summary>
    /// <remarks>Excluded by name rather than by matching a naming convention, so a flag arriving under any
    /// name — <c>IsRip7212Enabled</c>, or whatever the next one is called — is swept unless listed here.</remarks>
    private static readonly FrozenSet<string> NotForkFlags = new[] { nameof(IReceiptSpec.ValidateReceipts) }.ToFrozenSet();

    /// <summary>Flags the concrete spec derives rather than stores, mapped to the flag that drives them.</summary>
    private static readonly Dictionary<string, string> FlagsDrivenByAnother = new()
    {
        [nameof(IReleaseSpec.BlockLevelAccessListsEnabled)] = nameof(IReleaseSpec.IsEip7928Enabled)
    };

    /// <summary>Every fork flag on the interface, including the ones its base interfaces contribute.</summary>
    private static IEnumerable<TestCaseData> EveryForkFlag()
    {
        Type[] declaringTypes = [typeof(IReleaseSpec), .. typeof(IReleaseSpec).GetInterfaces()];
        HashSet<string> seen = [];

        foreach (Type declaringType in declaringTypes)
        {
            foreach (PropertyInfo property in declaringType.GetProperties())
            {
                if (property.PropertyType == typeof(bool)
                    && !NotForkFlags.Contains(property.Name)
                    && seen.Add(property.Name))
                {
                    yield return new TestCaseData(property).SetArgDisplayNames(property.Name);
                }
            }
        }
    }

    /// <summary>An external spec that implements nothing beyond <see cref="ReleaseSpec"/> still compiles,
    /// and reports every fork flag off.</summary>
    /// <remarks>This is half of what the interface's remark promises implementors, and it is why adding a
    /// fork flag does not break a downstream spec built this way. It sweeps every flag rather than the
    /// newest ones so a flag added later cannot quietly arrive switched on.</remarks>
    [TestCaseSource(nameof(EveryForkFlag))]
    public void Minimal_external_spec_reports_every_fork_flag_off(PropertyInfo flag)
    {
        IReleaseSpec spec = new MinimalExternalSpec();

        Assert.That(flag.GetValue(spec), Is.False, $"{flag.Name} is on for a spec that never set it");
    }

    /// <summary>An external spec that decorates another still compiles, and reports what it wraps.</summary>
    /// <remarks>The other half, and the reason the flags carry no <c>=&gt; false</c> defaults: a forwarding
    /// implementor that inherited one would report an enabled EIP as disabled. A flag that stops being
    /// forwarded, or is forwarded from a neighbouring flag, fails here instead of diverging on chain.</remarks>
    [TestCaseSource(nameof(EveryForkFlag))]
    public void Minimal_external_decorator_forwards_every_fork_flag(PropertyInfo flag)
    {
        // Only the flag under test is switched on, so a line forwarding a neighbour reports false. Setting
        // IsEip4844Enabled also switches IsEip1559Enabled on, leaving that one pair indistinguishable.
        ReleaseSpec enabled = new();
        PropertyInfo? driver = typeof(ReleaseSpec).GetProperty(FlagsDrivenByAnother.GetValueOrDefault(flag.Name, flag.Name));

        Assert.That(driver?.CanWrite, Is.True, $"{flag.Name} has no writable flag behind it, so the sweep proves nothing");
        driver!.SetValue(enabled, true);

        Assert.That(flag.GetValue(enabled), Is.True, $"{flag.Name} could not be switched on, so the sweep proves nothing");
        Assert.That(flag.GetValue(new MinimalExternalDecorator(enabled)), Is.True, $"{flag.Name} is not forwarded");
    }

    private sealed class MinimalExternalSpec : ReleaseSpec;

    private sealed class MinimalExternalDecorator(IReleaseSpec spec) : ReleaseSpecDecorator(spec);

    private sealed class SpecWithPrecompileAt(Address address) : ReleaseSpec
    {
        public override FrozenSet<AddressAsKey> BuildPrecompilesCache() => new AddressAsKey[] { address }.ToFrozenSet();
    }
}
