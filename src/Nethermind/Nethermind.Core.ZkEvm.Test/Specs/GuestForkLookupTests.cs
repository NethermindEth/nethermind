// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Core.ZkEvm.Test.Specs;

/// <summary>The schedule scan behind <see cref="ForkScheduleSpecProvider.TryGetForkSpec"/> in the zkEVM build.</summary>
public class GuestForkLookupTests
{
    [TestCase("Frontier")]
    [TestCase("osaka")]
    [TestCase("AMSTERDAM")]
    [TestCase("Bogota")]
    public void Finds_a_scheduled_fork_by_name_ignoring_case(string name)
    {
#pragma warning disable NUnit2045 // A successful lookup is required before dereferencing the fork specification.
        Assert.That(MainnetSpecProvider.Instance.TryGetForkSpec(name, out IReleaseSpec? spec), Is.True);
#pragma warning restore NUnit2045
        Assert.That(spec!.Name, Is.EqualTo(name).IgnoreCase);
    }

    [Test]
    public void Returns_the_scheduled_instance() =>
        Assert.That(MainnetSpecProvider.Instance.TryGetForkSpec("Amsterdam", out IReleaseSpec? spec) ? spec : null, Is.SameAs(Amsterdam.Instance));

    [Test]
    public void Misses_an_unscheduled_fork()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(MainnetSpecProvider.Instance.TryGetForkSpec("Olympic", out IReleaseSpec? spec), Is.False);
            Assert.That(spec, Is.Null);
        }
    }
}
