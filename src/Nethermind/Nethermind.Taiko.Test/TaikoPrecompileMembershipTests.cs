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
/// 0x10002, which makes it the chain that exercises membership beyond the mask and the index array.</summary>
[TestFixture]
public class TaikoPrecompileMembershipTests
{
    /// <summary>Every Taiko fork spec, with the flags that register the two far precompiles set.</summary>
    /// <remarks>The flags are set before <c>Precompiles</c> is first read, which is what builds the set, so
    /// each fork is swept with its full registration rather than its default one. Nothing is filtered out:
    /// a fork this cannot construct has to break the sweep, since one that quietly shrinks stops defending
    /// the newest registration — the one most likely to need it.</remarks>
    private static IEnumerable<TestCaseData> TaikoForks() =>
        typeof(ITaikoReleaseSpec).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(ITaikoReleaseSpec).IsAssignableFrom(t))
            .Select(t =>
            {
                object spec = Activator.CreateInstance(t)!;
                // Get-only on the interface, settable on every fork class that declares them.
                t.GetProperty(nameof(ITaikoReleaseSpec.IsRip7728Enabled))!.SetValue(spec, true);
                t.GetProperty(nameof(ITaikoReleaseSpec.IsL1StaticCallEnabled))!.SetValue(spec, true);
                return new TestCaseData((ITaikoReleaseSpec)spec).SetArgDisplayNames(t.Name);
            });

    /// <summary>Every registered precompile has to be recognised, at every Taiko fork.</summary>
    /// <remarks>Taiko's two sit above the 64-bit mask and above the index array, so they are the in-tree
    /// case for the set fallback; the shape invariant they also depend on is enforced where the set is
    /// built, which throws before an assertion here could see it.</remarks>
    [TestCaseSource(nameof(TaikoForks))]
    public void Every_registered_precompile_is_recognised(ITaikoReleaseSpec taikoSpec)
    {
        IReleaseSpec spec = taikoSpec;

        Assert.That(spec.Precompiles, Does.Contain((AddressAsKey)L1SloadPrecompile.Address)
            .And.Contain((AddressAsKey)L1StaticCallPrecompile.Address), "the far precompiles must be in the sweep");

        foreach (AddressAsKey key in spec.Precompiles)
        {
            Assert.That(spec.IsPrecompile(key), Is.True, $"{(Address)key} is registered but not recognised");
        }
    }
}
