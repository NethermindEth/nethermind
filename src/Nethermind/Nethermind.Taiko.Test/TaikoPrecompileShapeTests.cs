// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Taiko.Precompiles;
using Nethermind.Taiko.TaikoSpec;
using NUnit.Framework;

namespace Nethermind.Taiko.Test;

/// <summary>Taiko registers the only in-tree precompiles outside Ethereum's range, at 0x10001 and
/// 0x10002, which makes it the chain that exercises the limits of resolving one by its number.</summary>
[TestFixture]
public class TaikoPrecompileShapeTests
{
    /// <summary>Every Taiko fork spec, with the flags that register the two far precompiles set.</summary>
    /// <remarks>The flags are set before <c>Precompiles</c> is first read, which is what builds the set,
    /// so each fork is swept with its full registration rather than its default one.</remarks>
    private static IEnumerable<TestCaseData> TaikoForks() =>
        typeof(ITaikoReleaseSpec).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(ITaikoReleaseSpec).IsAssignableFrom(t)
                        && t.GetConstructor(Type.EmptyTypes) is not null)
            .Select(t =>
            {
                object spec = Activator.CreateInstance(t)!;
                // Get-only on the interface, settable on every fork class that declares them.
                t.GetProperty(nameof(ITaikoReleaseSpec.IsRip7728Enabled))!.SetValue(spec, true);
                t.GetProperty(nameof(ITaikoReleaseSpec.IsL1StaticCallEnabled))!.SetValue(spec, true);
                return new TestCaseData((ITaikoReleaseSpec)spec).SetArgDisplayNames(t.Name);
            });

    /// <summary>Every registered precompile must clear the shape guard, or membership cannot see it.</summary>
    /// <remarks>Membership rejects an address whose top sixteen bytes are not all zero, capping a
    /// precompile number at <see cref="uint.MaxValue"/>. Taiko's sit far above the indexed run but well
    /// inside that cap; a future registration above it would resolve as an ordinary account, silently, so
    /// the invariant is asserted rather than assumed.</remarks>
    [TestCaseSource(nameof(TaikoForks))]
    public void Every_registered_precompile_clears_the_shape_guard(ITaikoReleaseSpec taikoSpec)
    {
        IReleaseSpec spec = taikoSpec;

        Assert.That(spec.Precompiles, Does.Contain((AddressAsKey)L1SloadPrecompile.Address)
            .And.Contain((AddressAsKey)L1StaticCallPrecompile.Address), "the far precompiles must be in the sweep");

        foreach (AddressAsKey key in spec.Precompiles)
        {
            Address address = key;
            Assert.That(address.CouldBePrecompile(), Is.True, $"{address} cannot be reached by IsPrecompile");
            Assert.That(spec.IsPrecompile(address), Is.True, $"{address} is registered but not recognised");
        }
    }
}
