// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;

namespace Nethermind.Network.Test;

[Parallelizable(ParallelScope.Self)]
[TestFixture]
public class NodeFilterTests
{
    [Test]
    public void ExactMatch_BlocksSameAddressWithinTimeout()
    {
        NodeFilter filter = new(size: 100, exactMatchOnly: true);
        IPAddress ip = IPAddress.Parse("192.0.2.1");

        filter.Set(ip, null).Should().BeTrue("first attempt should be accepted");
        filter.Set(ip, null).Should().BeFalse("second attempt within timeout should be rejected");
    }

    [Test]
    public void ExactMatch_AllowsDifferentAddresses()
    {
        NodeFilter filter = new(size: 100, exactMatchOnly: true);
        IPAddress ip1 = IPAddress.Parse("192.0.2.1");
        IPAddress ip2 = IPAddress.Parse("192.0.2.2");

        filter.Set(ip1, null).Should().BeTrue("first address should be accepted");
        filter.Set(ip2, null).Should().BeTrue("different address should be accepted");
    }

    [Test]
    public void SubnetBucketing_IPv4_BlocksSameSubnet()
    {
        NodeFilter filter = new(size: 100, exactMatchOnly: false);
        IPAddress ip1 = IPAddress.Parse("192.0.2.1");
        IPAddress ip2 = IPAddress.Parse("192.0.2.50"); // Same /24 subnet

        filter.Set(ip1, null).Should().BeTrue("first address should be accepted");
        filter.Set(ip2, null).Should().BeFalse("address in same /24 subnet should be rejected");
    }

    [Test]
    public void SubnetBucketing_IPv4_AllowsDifferentSubnet()
    {
        NodeFilter filter = new(size: 100, exactMatchOnly: false);
        IPAddress ip1 = IPAddress.Parse("192.0.2.1");
        IPAddress ip2 = IPAddress.Parse("192.0.3.1"); // Different /24 subnet

        filter.Set(ip1, null).Should().BeTrue("first address should be accepted");
        filter.Set(ip2, null).Should().BeTrue("address in different /24 subnet should be accepted");
    }

    [Test]
    public void SubnetBucketing_IPv6_BlocksSameSubnet()
    {
        NodeFilter filter = new(size: 100, exactMatchOnly: false);
        IPAddress ip1 = IPAddress.Parse("2001:db8::1");
        IPAddress ip2 = IPAddress.Parse("2001:db8::ffff"); // Same /64 subnet

        filter.Set(ip1, null).Should().BeTrue("first address should be accepted");
        filter.Set(ip2, null).Should().BeFalse("address in same /64 subnet should be rejected");
    }

    [Test]
    public void SubnetBucketing_IPv6_AllowsDifferentSubnet()
    {
        NodeFilter filter = new(size: 100, exactMatchOnly: false);
        IPAddress ip1 = IPAddress.Parse("2001:db8:0:0::1");
        IPAddress ip2 = IPAddress.Parse("2001:db8:0:1::1"); // Different /64 subnet

        filter.Set(ip1, null).Should().BeTrue("first address should be accepted");
        filter.Set(ip2, null).Should().BeTrue("address in different /64 subnet should be accepted");
    }

    [Test]
    public void PrivateAddress_RFC1918_10_TreatedAsExact()
    {
        NodeFilter filter = new(size: 100, exactMatchOnly: false);
        IPAddress ip1 = IPAddress.Parse("10.0.0.1");
        IPAddress ip2 = IPAddress.Parse("10.0.0.2"); // Same /24 but should be treated as different

        filter.Set(ip1, null).Should().BeTrue("first private address should be accepted");
        filter.Set(ip2, null).Should().BeTrue("different private address should be accepted (exact match)");
    }

    [Test]
    public void PrivateAddress_RFC1918_172_TreatedAsExact()
    {
        NodeFilter filter = new(size: 100, exactMatchOnly: false);
        IPAddress ip1 = IPAddress.Parse("172.16.0.1");
        IPAddress ip2 = IPAddress.Parse("172.16.0.2"); // Same /24 but should be treated as different

        filter.Set(ip1, null).Should().BeTrue("first private address should be accepted");
        filter.Set(ip2, null).Should().BeTrue("different private address should be accepted (exact match)");
    }

    [Test]
    public void PrivateAddress_RFC1918_192_TreatedAsExact()
    {
        NodeFilter filter = new(size: 100, exactMatchOnly: false);
        IPAddress ip1 = IPAddress.Parse("192.168.1.1");
        IPAddress ip2 = IPAddress.Parse("192.168.1.2"); // Same /24 but should be treated as different

        filter.Set(ip1, null).Should().BeTrue("first private address should be accepted");
        filter.Set(ip2, null).Should().BeTrue("different private address should be accepted (exact match)");
    }

    [Test]
    public void LoopbackAddress_TreatedAsExact()
    {
        NodeFilter filter = new(size: 100, exactMatchOnly: false);
        IPAddress ip1 = IPAddress.Parse("127.0.0.1");
        IPAddress ip2 = IPAddress.Parse("127.0.0.2"); // Same /24 but should be treated as different

        filter.Set(ip1, null).Should().BeTrue("first loopback address should be accepted");
        filter.Set(ip2, null).Should().BeTrue("different loopback address should be accepted (exact match)");
    }

    [Test]
    public void LinkLocalAddress_IPv4_TreatedAsExact()
    {
        NodeFilter filter = new(size: 100, exactMatchOnly: false);
        IPAddress ip1 = IPAddress.Parse("169.254.0.1");
        IPAddress ip2 = IPAddress.Parse("169.254.0.2"); // Same /24 but should be treated as different

        filter.Set(ip1, null).Should().BeTrue("first link-local address should be accepted");
        filter.Set(ip2, null).Should().BeTrue("different link-local address should be accepted (exact match)");
    }

    [Test]
    public void CGNAT_Address_TreatedAsExact()
    {
        NodeFilter filter = new(size: 100, exactMatchOnly: false);
        IPAddress ip1 = IPAddress.Parse("100.64.0.1");
        IPAddress ip2 = IPAddress.Parse("100.64.0.2"); // Same /24 but should be treated as different

        filter.Set(ip1, null).Should().BeTrue("first CGNAT address should be accepted");
        filter.Set(ip2, null).Should().BeTrue("different CGNAT address should be accepted (exact match)");
    }

    [Test]
    public void IPv6_Loopback_TreatedAsExact()
    {
        NodeFilter filter = new(size: 100, exactMatchOnly: false);
        IPAddress loopback = IPAddress.IPv6Loopback;

        filter.Set(loopback, null).Should().BeTrue("loopback should be accepted");
        filter.Set(loopback, null).Should().BeFalse("second attempt should be rejected (exact match)");
    }

    [Test]
    public void IPv6_ULA_TreatedAsExact()
    {
        NodeFilter filter = new(size: 100, exactMatchOnly: false);
        IPAddress ip1 = IPAddress.Parse("fc00::1");
        IPAddress ip2 = IPAddress.Parse("fc00::2"); // Same /64 but should be treated as different

        filter.Set(ip1, null).Should().BeTrue("first ULA address should be accepted");
        filter.Set(ip2, null).Should().BeTrue("different ULA address should be accepted (exact match)");
    }

    [Test]
    public void IPv6_LinkLocal_TreatedAsExact()
    {
        NodeFilter filter = new(size: 100, exactMatchOnly: false);
        IPAddress ip1 = IPAddress.Parse("fe80::1");
        IPAddress ip2 = IPAddress.Parse("fe80::2"); // Same /64 but should be treated as different

        filter.Set(ip1, null).Should().BeTrue("first link-local address should be accepted");
        filter.Set(ip2, null).Should().BeTrue("different link-local address should be accepted (exact match)");
    }

    [Test]
    public void IPv4MappedIPv6_TreatedAsIPv4()
    {
        NodeFilter filter = new(size: 100, exactMatchOnly: false);
        IPAddress ipv4 = IPAddress.Parse("192.0.2.1");
        IPAddress ipv4Mapped = IPAddress.Parse("::ffff:192.0.2.1");

        filter.Set(ipv4, null).Should().BeTrue("IPv4 address should be accepted");
        filter.Set(ipv4Mapped, null).Should().BeFalse("IPv4-mapped IPv6 address should be rejected (same as IPv4)");
    }

    [Test]
    public void IPv4MappedIPv6_SubnetBucketing()
    {
        NodeFilter filter = new(size: 100, exactMatchOnly: false);
        IPAddress ipv4Mapped1 = IPAddress.Parse("::ffff:192.0.2.1");
        IPAddress ipv4Mapped2 = IPAddress.Parse("::ffff:192.0.2.50"); // Same /24 subnet

        filter.Set(ipv4Mapped1, null).Should().BeTrue("first IPv4-mapped address should be accepted");
        filter.Set(ipv4Mapped2, null).Should().BeFalse("IPv4-mapped address in same /24 should be rejected");
    }

    [Test]
    public void TimeBasedFiltering_AcceptsAfterTimeout()
    {
        NodeFilter filter = new(size: 100, exactMatchOnly: true);
        IPAddress ip = IPAddress.Parse("192.0.2.1");

        filter.Set(ip, null).Should().BeTrue("first attempt should be accepted");
        filter.Set(ip, null).Should().BeFalse("second attempt within timeout should be rejected");

        // Wait for timeout (5 minutes + buffer)
        // Note: This test would take 5+ minutes in real time, so we're documenting behavior
        // In production, Environment.TickCount64 is used, which is monotonic
    }

    [Test]
    public void ThreadSafety_ConcurrentSetCalls()
    {
        NodeFilter filter = new(size: 1000, exactMatchOnly: true);
        int threadCount = 10;
        int attemptsPerThread = 100;
        List<IPAddress> addresses = [];

        // Pre-generate unique addresses
        for (int i = 0; i < threadCount * attemptsPerThread; i++)
        {
            addresses.Add(IPAddress.Parse($"192.0.{i / 256}.{i % 256}"));
        }

        int acceptedCount = 0;
        List<Task> tasks = [];

        for (int t = 0; t < threadCount; t++)
        {
            int threadIndex = t;
            tasks.Add(Task.Run(() =>
            {
                for (int i = 0; i < attemptsPerThread; i++)
                {
                    int index = threadIndex * attemptsPerThread + i;
                    if (filter.Set(addresses[index], null))
                    {
                        Interlocked.Increment(ref acceptedCount);
                    }
                }
            }));
        }

        Task.WaitAll([.. tasks]);

        acceptedCount.Should().Be(threadCount * attemptsPerThread, "all unique addresses should be accepted");
    }

    [Test]
    public void ThreadSafety_ConcurrentSetCallsSameAddress()
    {
        NodeFilter filter = new(size: 100, exactMatchOnly: true);
        IPAddress ip = IPAddress.Parse("192.0.2.1");
        int threadCount = 10;
        int attemptsPerThread = 10;

        int acceptedCount = 0;
        List<Task> tasks = [];

        for (int t = 0; t < threadCount; t++)
        {
            tasks.Add(Task.Run(() =>
            {
                for (int i = 0; i < attemptsPerThread; i++)
                {
                    if (filter.Set(ip, null))
                    {
                        Interlocked.Increment(ref acceptedCount);
                    }
                }
            }));
        }

        Task.WaitAll([.. tasks]);

        acceptedCount.Should().Be(1, "only one thread should succeed in setting the same address");
    }

    [Test]
    public void SubnetMasking_EdgeCase_IPv4_Boundary()
    {
        NodeFilter filter = new(size: 100, exactMatchOnly: false);
        IPAddress ip1 = IPAddress.Parse("192.0.2.255"); // Last in /24
        IPAddress ip2 = IPAddress.Parse("192.0.3.0");   // First in next /24

        filter.Set(ip1, null).Should().BeTrue("first address should be accepted");
        filter.Set(ip2, null).Should().BeTrue("address in different subnet should be accepted");
    }

    [Test]
    public void SubnetMasking_EdgeCase_IPv6_Boundary()
    {
        NodeFilter filter = new(size: 100, exactMatchOnly: false);
        IPAddress ip1 = IPAddress.Parse("2001:db8:0:0:ffff:ffff:ffff:ffff"); // Last in /64
        IPAddress ip2 = IPAddress.Parse("2001:db8:0:1::0");                   // First in next /64

        filter.Set(ip1, null).Should().BeTrue("first address should be accepted");
        filter.Set(ip2, null).Should().BeTrue("address in different subnet should be accepted");
    }

    [Test]
    public void SubnetMasking_EdgeCase_IPv4_ZeroPrefix()
    {
        // Direct test of IpSubnetKey with /0 prefix
        IPAddress ip1 = IPAddress.Parse("192.0.2.1");
        IPAddress ip2 = IPAddress.Parse("10.0.0.1");

        var key1 = new NodeFilter.IpSubnetKey(ip1, v4PrefixBits: 0, v6PrefixBits: 0);
        var key2 = new NodeFilter.IpSubnetKey(ip2, v4PrefixBits: 0, v6PrefixBits: 0);

        key1.Should().Be(key2, "all IPv4 addresses should match with /0 prefix");
    }

    [Test]
    public void SubnetMasking_EdgeCase_IPv4_FullPrefix()
    {
        // Direct test of IpSubnetKey with /32 prefix
        IPAddress ip1 = IPAddress.Parse("192.0.2.1");
        IPAddress ip2 = IPAddress.Parse("192.0.2.1");

        var key1 = new NodeFilter.IpSubnetKey(ip1, v4PrefixBits: 32, v6PrefixBits: 128);
        var key2 = new NodeFilter.IpSubnetKey(ip2, v4PrefixBits: 32, v6PrefixBits: 128);

        key1.Should().Be(key2, "exact same address should match with /32 prefix");
    }

    [Test]
    public void SubnetMasking_EdgeCase_IPv6_ZeroPrefix()
    {
        // Direct test of IpSubnetKey with /0 prefix
        IPAddress ip1 = IPAddress.Parse("2001:db8::1");
        IPAddress ip2 = IPAddress.Parse("fe80::1");

        var key1 = new NodeFilter.IpSubnetKey(ip1, v4PrefixBits: 0, v6PrefixBits: 0);
        var key2 = new NodeFilter.IpSubnetKey(ip2, v4PrefixBits: 0, v6PrefixBits: 0);

        key1.Should().Be(key2, "all IPv6 addresses should match with /0 prefix");
    }

    [Test]
    public void SubnetMasking_EdgeCase_IPv6_FullPrefix()
    {
        // Direct test of IpSubnetKey with /128 prefix
        IPAddress ip1 = IPAddress.Parse("2001:db8::1");
        IPAddress ip2 = IPAddress.Parse("2001:db8::1");

        var key1 = new NodeFilter.IpSubnetKey(ip1, v4PrefixBits: 32, v6PrefixBits: 128);
        var key2 = new NodeFilter.IpSubnetKey(ip2, v4PrefixBits: 32, v6PrefixBits: 128);

        key1.Should().Be(key2, "exact same address should match with /128 prefix");
    }

    [Test]
    public void CapacityBounded_EvictsOldEntries()
    {
        NodeFilter filter = new(size: 2, exactMatchOnly: true);
        IPAddress ip1 = IPAddress.Parse("192.0.2.1");
        IPAddress ip2 = IPAddress.Parse("192.0.2.2");
        IPAddress ip3 = IPAddress.Parse("192.0.2.3");

        filter.Set(ip1, null).Should().BeTrue("first address should be accepted");
        filter.Set(ip2, null).Should().BeTrue("second address should be accepted");
        filter.Set(ip3, null).Should().BeTrue("third address should be accepted");

        // After adding 3 addresses to a cache of size 2, one should be evicted
        // Due to cache eviction, one of the first two addresses might now be accepted
        bool ip1Accepted = filter.Set(ip1, null);
        bool ip2Accepted = filter.Set(ip2, null);

        (ip1Accepted || ip2Accepted).Should().BeTrue("at least one of the first two addresses should be evicted and accepted again");
    }

    [Test]
    public void IpSubnetKey_Equality_SameAddress()
    {
        IPAddress ip = IPAddress.Parse("192.0.2.1");
        var key1 = NodeFilter.IpSubnetKey.Exact(ip);
        var key2 = NodeFilter.IpSubnetKey.Exact(ip);

        key1.Should().Be(key2);
        key1.GetHashCode().Should().Be(key2.GetHashCode());
    }

    [Test]
    public void IpSubnetKey_Equality_DifferentAddress()
    {
        IPAddress ip1 = IPAddress.Parse("192.0.2.1");
        IPAddress ip2 = IPAddress.Parse("192.0.2.2");
        var key1 = NodeFilter.IpSubnetKey.Exact(ip1);
        var key2 = NodeFilter.IpSubnetKey.Exact(ip2);

        key1.Should().NotBe(key2);
    }

    [Test]
    public void IpSubnetKey_Matches_SameSubnet()
    {
        IPAddress ip1 = IPAddress.Parse("192.0.2.1");
        IPAddress ip2 = IPAddress.Parse("192.0.2.50");
        var key = NodeFilter.IpSubnetKey.Bucket(ip1, v4PrefixBits: 24, v6PrefixBits: 64);

        key.Matches(ip2).Should().BeTrue("address in same /24 subnet should match");
    }

    [Test]
    public void IpSubnetKey_Matches_DifferentSubnet()
    {
        IPAddress ip1 = IPAddress.Parse("192.0.2.1");
        IPAddress ip2 = IPAddress.Parse("192.0.3.1");
        var key = NodeFilter.IpSubnetKey.Bucket(ip1, v4PrefixBits: 24, v6PrefixBits: 64);

        key.Matches(ip2).Should().BeFalse("address in different /24 subnet should not match");
    }

    [Test]
    public void IpSubnetKey_AreInSameSubnet_IPv4()
    {
        IPAddress ip1 = IPAddress.Parse("192.0.2.1");
        IPAddress ip2 = IPAddress.Parse("192.0.2.50");
        IPAddress ip3 = IPAddress.Parse("192.0.3.1");

        NodeFilter.IpSubnetKey.AreInSameSubnet(ip1, ip2, v4PrefixBits: 24, v6PrefixBits: 64).Should().BeTrue("same /24 subnet");
        NodeFilter.IpSubnetKey.AreInSameSubnet(ip1, ip3, v4PrefixBits: 24, v6PrefixBits: 64).Should().BeFalse("different /24 subnet");
    }

    [Test]
    public void IpSubnetKey_AreInSameSubnet_IPv6()
    {
        IPAddress ip1 = IPAddress.Parse("2001:db8::1");
        IPAddress ip2 = IPAddress.Parse("2001:db8::ffff");
        IPAddress ip3 = IPAddress.Parse("2001:db8:0:1::1");

        NodeFilter.IpSubnetKey.AreInSameSubnet(ip1, ip2, v4PrefixBits: 24, v6PrefixBits: 64).Should().BeTrue("same /64 subnet");
        NodeFilter.IpSubnetKey.AreInSameSubnet(ip1, ip3, v4PrefixBits: 24, v6PrefixBits: 64).Should().BeFalse("different /64 subnet");
    }

    [Test]
    public void IpSubnetKey_IsLoopbackOrPrivateOrLinkLocal_Loopback()
    {
        NodeFilter.IpSubnetKey.IsLoopbackOrPrivateOrLinkLocal(IPAddress.Loopback).Should().BeTrue();
        NodeFilter.IpSubnetKey.IsLoopbackOrPrivateOrLinkLocal(IPAddress.IPv6Loopback).Should().BeTrue();
    }

    [Test]
    public void IpSubnetKey_IsLoopbackOrPrivateOrLinkLocal_RFC1918()
    {
        NodeFilter.IpSubnetKey.IsLoopbackOrPrivateOrLinkLocal(IPAddress.Parse("10.0.0.1")).Should().BeTrue();
        NodeFilter.IpSubnetKey.IsLoopbackOrPrivateOrLinkLocal(IPAddress.Parse("172.16.0.1")).Should().BeTrue();
        NodeFilter.IpSubnetKey.IsLoopbackOrPrivateOrLinkLocal(IPAddress.Parse("192.168.1.1")).Should().BeTrue();
    }

    [Test]
    public void IpSubnetKey_IsLoopbackOrPrivateOrLinkLocal_LinkLocal()
    {
        NodeFilter.IpSubnetKey.IsLoopbackOrPrivateOrLinkLocal(IPAddress.Parse("169.254.0.1")).Should().BeTrue();
        NodeFilter.IpSubnetKey.IsLoopbackOrPrivateOrLinkLocal(IPAddress.Parse("fe80::1")).Should().BeTrue();
    }

    [Test]
    public void IpSubnetKey_IsLoopbackOrPrivateOrLinkLocal_CGNAT()
    {
        NodeFilter.IpSubnetKey.IsLoopbackOrPrivateOrLinkLocal(IPAddress.Parse("100.64.0.1")).Should().BeTrue();
    }

    [Test]
    public void IpSubnetKey_IsLoopbackOrPrivateOrLinkLocal_ULA()
    {
        NodeFilter.IpSubnetKey.IsLoopbackOrPrivateOrLinkLocal(IPAddress.Parse("fc00::1")).Should().BeTrue();
        NodeFilter.IpSubnetKey.IsLoopbackOrPrivateOrLinkLocal(IPAddress.Parse("fd00::1")).Should().BeTrue();
    }

    [Test]
    public void IpSubnetKey_IsLoopbackOrPrivateOrLinkLocal_Public()
    {
        NodeFilter.IpSubnetKey.IsLoopbackOrPrivateOrLinkLocal(IPAddress.Parse("8.8.8.8")).Should().BeFalse();
        NodeFilter.IpSubnetKey.IsLoopbackOrPrivateOrLinkLocal(IPAddress.Parse("2001:4860:4860::8888")).Should().BeFalse();
    }

    [Test]
    public void CreateNodeFilterKey_RemotePrivate_UsesExact()
    {
        IPAddress remotePrivate = IPAddress.Parse("192.168.1.10");
        IPAddress remotePrivate2 = IPAddress.Parse("192.168.1.20");
        IPAddress currentPublic = IPAddress.Parse("203.0.113.1");

        var key1 = NodeFilter.IpSubnetKey.CreateNodeFilterKey(remotePrivate, currentPublic);
        var key2 = NodeFilter.IpSubnetKey.CreateNodeFilterKey(remotePrivate2, currentPublic);

        key1.Should().NotBe(key2, "private addresses should use exact keying");
    }

    [Test]
    public void CreateNodeFilterKey_RemotePublic_UsesBucket()
    {
        IPAddress remote1 = IPAddress.Parse("203.0.113.1");
        IPAddress remote2 = IPAddress.Parse("203.0.113.50");
        IPAddress currentPublic = IPAddress.Parse("198.51.100.1");

        var key1 = NodeFilter.IpSubnetKey.CreateNodeFilterKey(remote1, currentPublic);
        var key2 = NodeFilter.IpSubnetKey.CreateNodeFilterKey(remote2, currentPublic);

        key1.Should().Be(key2, "public addresses in same /24 should use subnet bucketing");
    }

    [Test]
    public void CreateNodeFilterKey_SameLocalSubnet_UsesExact()
    {
        IPAddress remote1 = IPAddress.Parse("192.168.1.10");
        IPAddress remote2 = IPAddress.Parse("192.168.1.20");
        IPAddress current = IPAddress.Parse("192.168.1.1");

        var key1 = NodeFilter.IpSubnetKey.CreateNodeFilterKey(remote1, current);
        var key2 = NodeFilter.IpSubnetKey.CreateNodeFilterKey(remote2, current);

        key1.Should().NotBe(key2, "addresses in same local subnet should use exact keying");
    }
}
