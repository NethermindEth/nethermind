// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Net;
using NUnit.Framework;

namespace Nethermind.Network.Test;

[Parallelizable(ParallelScope.All)]
public class NormalizingIpAddressComparerTests
{
    private static readonly NormalizingIpAddressComparer Comparer = NormalizingIpAddressComparer.Instance;

    private static IEnumerable<TestCaseData> EqualCases()
    {
        yield return new TestCaseData(IPAddress.Parse("1.2.3.4"), IPAddress.Parse("1.2.3.4")).SetName("Same IPv4");
        yield return new TestCaseData(IPAddress.Parse("1.2.3.4"), IPAddress.Parse("::ffff:1.2.3.4")).SetName("IPv4 vs its IPv4-mapped IPv6");
        yield return new TestCaseData(IPAddress.Parse("::ffff:1.2.3.4"), IPAddress.Parse("::ffff:1.2.3.4")).SetName("Same IPv4-mapped IPv6");
        yield return new TestCaseData(IPAddress.Parse("2001:db8::1"), IPAddress.Parse("2001:db8::1")).SetName("Same IPv6");
    }

    private static IEnumerable<TestCaseData> NotEqualCases()
    {
        yield return new TestCaseData(IPAddress.Parse("1.2.3.4"), IPAddress.Parse("1.2.3.5")).SetName("Different IPv4");
        yield return new TestCaseData(IPAddress.Parse("1.2.3.4"), IPAddress.Parse("::1.2.3.4")).SetName("IPv4 vs IPv4-compatible (not mapped) IPv6");
        yield return new TestCaseData(IPAddress.Parse("2001:db8::1"), IPAddress.Parse("2001:db8::2")).SetName("Different IPv6");
        yield return new TestCaseData(IPAddress.Parse("::ffff:1.2.3.4"), IPAddress.Parse("2001:db8::1")).SetName("IPv4-mapped IPv6 vs real IPv6");
    }

    [TestCaseSource(nameof(EqualCases))]
    public void Equal_addresses_compare_equal_and_share_hash(IPAddress a, IPAddress b)
    {
        Assert.That(Comparer.Equals(a, b), Is.True);
        Assert.That(Comparer.Equals(b, a), Is.True);
        Assert.That(Comparer.GetHashCode(a), Is.EqualTo(Comparer.GetHashCode(b)));
    }

    [TestCaseSource(nameof(NotEqualCases))]
    public void Different_addresses_do_not_compare_equal(IPAddress a, IPAddress b)
    {
        Assert.That(Comparer.Equals(a, b), Is.False);
        Assert.That(Comparer.Equals(b, a), Is.False);
    }

    [Test]
    public void Handles_null_and_reference_equality()
    {
        IPAddress ip = IPAddress.Parse("1.2.3.4");
        Assert.That(Comparer.Equals(null, null), Is.True);
        Assert.That(Comparer.Equals(ip, null), Is.False);
        Assert.That(Comparer.Equals(null, ip), Is.False);
        Assert.That(Comparer.Equals(ip, ip), Is.True);
    }
}
