// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
        NodeFilter filter = new(size: 100, exactMatchOnly: true, currentIp: null);
        IPAddress ip = IPAddress.Parse("192.0.2.1");

        filter.TryAccept(ip).Should().BeTrue("first attempt should be accepted");
        filter.TryAccept(ip).Should().BeFalse("second attempt within timeout should be rejected");
    }

    [Test]
    public void ExactMatch_AllowsDifferentAddresses()
    {
        NodeFilter filter = new(size: 100, exactMatchOnly: true, currentIp: null);
        IPAddress ip1 = IPAddress.Parse("192.0.2.1");
        IPAddress ip2 = IPAddress.Parse("192.0.2.2");

        filter.TryAccept(ip1).Should().BeTrue("first address should be accepted");
        filter.TryAccept(ip2).Should().BeTrue("different address should be accepted");
    }

    [TestCase("192.0.2.1", "192.0.2.50", false, Description = "Same /24 subnet blocked")]
    [TestCase("192.0.2.1", "192.0.3.1", true, Description = "Different /24 subnet allowed")]
    [TestCase("2001:db8::1", "2001:db8::ffff", false, Description = "Same /64 IPv6 subnet blocked")]
    [TestCase("2001:db8:0:0::1", "2001:db8:0:1::1", true, Description = "Different /64 IPv6 subnet allowed")]
    public void SubnetBucketing_AcceptsOrRejects(string first, string second, bool secondAccepted)
    {
        NodeFilter filter = new(size: 100, exactMatchOnly: false, currentIp: null);
        filter.TryAccept(IPAddress.Parse(first)).Should().BeTrue("first address should be accepted");
        filter.TryAccept(IPAddress.Parse(second)).Should().Be(secondAccepted);
    }

    [TestCase("10.0.0.1", "10.0.0.2", Description = "RFC1918 10.x")]
    [TestCase("172.16.0.1", "172.16.0.2", Description = "RFC1918 172.16.x")]
    [TestCase("192.168.1.1", "192.168.1.2", Description = "RFC1918 192.168.x")]
    [TestCase("127.0.0.1", "127.0.0.2", Description = "Loopback")]
    [TestCase("169.254.0.1", "169.254.0.2", Description = "IPv4 link-local")]
    [TestCase("100.64.0.1", "100.64.0.2", Description = "CGNAT")]
    [TestCase("fc00::1", "fc00::2", Description = "IPv6 ULA")]
    [TestCase("fe80::1", "fe80::2", Description = "IPv6 link-local")]
    public void PrivateOrLocalAddress_TreatedAsExact(string first, string second)
    {
        NodeFilter filter = new(size: 100, exactMatchOnly: false, currentIp: null);
        filter.TryAccept(IPAddress.Parse(first)).Should().BeTrue("first address should be accepted");
        filter.TryAccept(IPAddress.Parse(second)).Should().BeTrue("different address should be accepted (exact match)");
    }

    [Test]
    public void IPv6_Loopback_TreatedAsExact()
    {
        NodeFilter filter = new(size: 100, exactMatchOnly: false, currentIp: null);
        IPAddress loopback = IPAddress.IPv6Loopback;

        filter.TryAccept(loopback).Should().BeTrue("loopback should be accepted");
        filter.TryAccept(loopback).Should().BeFalse("second attempt should be rejected (exact match)");
    }

    [Test]
    public void IPv4MappedIPv6_TreatedAsIPv4()
    {
        NodeFilter filter = new(size: 100, exactMatchOnly: false, currentIp: null);
        IPAddress ipv4 = IPAddress.Parse("192.0.2.1");
        IPAddress ipv4Mapped = IPAddress.Parse("::ffff:192.0.2.1");

        filter.TryAccept(ipv4).Should().BeTrue("IPv4 address should be accepted");
        filter.TryAccept(ipv4Mapped).Should().BeFalse("IPv4-mapped IPv6 address should be rejected (same as IPv4)");
    }

    [Test]
    public void IPv4MappedIPv6_SubnetBucketing()
    {
        NodeFilter filter = new(size: 100, exactMatchOnly: false, currentIp: null);
        IPAddress ipv4Mapped1 = IPAddress.Parse("::ffff:192.0.2.1");
        IPAddress ipv4Mapped2 = IPAddress.Parse("::ffff:192.0.2.50");

        filter.TryAccept(ipv4Mapped1).Should().BeTrue("first IPv4-mapped address should be accepted");
        filter.TryAccept(ipv4Mapped2).Should().BeFalse("IPv4-mapped address in same /24 should be rejected");
    }

    [Test]
    public void ThreadSafety_ConcurrentSetCalls()
    {
        NodeFilter filter = new(size: 1000, exactMatchOnly: true, currentIp: null);
        int threadCount = 10;
        int attemptsPerThread = 100;
        List<IPAddress> addresses = [];

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
                    if (filter.TryAccept(addresses[index]))
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
        NodeFilter filter = new(size: 100, exactMatchOnly: true, currentIp: null);
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
                    if (filter.TryAccept(ip))
                    {
                        Interlocked.Increment(ref acceptedCount);
                    }
                }
            }));
        }

        Task.WaitAll([.. tasks]);

        // At least one call should succeed, but not all attempts should be accepted for the same IP
        acceptedCount.Should().BeGreaterThan(0, "at least one concurrent attempt should be accepted");
        acceptedCount.Should().BeLessThan(threadCount * attemptsPerThread, "not all concurrent attempts should be accepted for the same address");
    }

    [TestCase("192.0.2.255", "192.0.3.0", Description = "IPv4 /24 boundary")]
    [TestCase("2001:db8:0:0:ffff:ffff:ffff:ffff", "2001:db8:0:1::0", Description = "IPv6 /64 boundary")]
    public void SubnetMasking_EdgeCase_Boundary(string first, string second)
    {
        NodeFilter filter = new(size: 100, exactMatchOnly: false, currentIp: null);
        filter.TryAccept(IPAddress.Parse(first)).Should().BeTrue("first address should be accepted");
        filter.TryAccept(IPAddress.Parse(second)).Should().BeTrue("address in different subnet should be accepted");
    }

    [Test]
    public void SubnetMasking_EdgeCase_IPv4_ZeroPrefix()
    {
        IPAddress ip1 = IPAddress.Parse("192.0.2.1");
        IPAddress ip2 = IPAddress.Parse("10.0.0.1");

        var key1 = new NodeFilter.IpSubnetKey(ip1, v4PrefixBits: 0, v6PrefixBits: 0);
        var key2 = new NodeFilter.IpSubnetKey(ip2, v4PrefixBits: 0, v6PrefixBits: 0);

        key1.Should().Be(key2, "all IPv4 addresses should match with /0 prefix");
    }

    [Test]
    public void SubnetMasking_EdgeCase_IPv4_FullPrefix()
    {
        IPAddress ip = IPAddress.Parse("192.0.2.1");

        var key1 = new NodeFilter.IpSubnetKey(ip, v4PrefixBits: 32, v6PrefixBits: 128);
        var key2 = new NodeFilter.IpSubnetKey(ip, v4PrefixBits: 32, v6PrefixBits: 128);

        key1.Should().Be(key2, "exact same address should match with /32 prefix");
    }

    [Test]
    public void SubnetMasking_EdgeCase_IPv6_ZeroPrefix()
    {
        IPAddress ip1 = IPAddress.Parse("2001:db8::1");
        IPAddress ip2 = IPAddress.Parse("fe80::1");

        var key1 = new NodeFilter.IpSubnetKey(ip1, v4PrefixBits: 0, v6PrefixBits: 0);
        var key2 = new NodeFilter.IpSubnetKey(ip2, v4PrefixBits: 0, v6PrefixBits: 0);

        key1.Should().Be(key2, "all IPv6 addresses should match with /0 prefix");
    }

    [Test]
    public void SubnetMasking_EdgeCase_IPv6_FullPrefix()
    {
        IPAddress ip = IPAddress.Parse("2001:db8::1");

        var key1 = new NodeFilter.IpSubnetKey(ip, v4PrefixBits: 32, v6PrefixBits: 128);
        var key2 = new NodeFilter.IpSubnetKey(ip, v4PrefixBits: 32, v6PrefixBits: 128);

        key1.Should().Be(key2, "exact same address should match with /128 prefix");
    }

    [Test]
    public void CapacityBounded_EvictsOldEntries()
    {
        NodeFilter filter = new(size: 2, exactMatchOnly: true, currentIp: null);
        IPAddress ip1 = IPAddress.Parse("192.0.2.1");
        IPAddress ip2 = IPAddress.Parse("192.0.2.2");
        IPAddress ip3 = IPAddress.Parse("192.0.2.3");

        filter.TryAccept(ip1).Should().BeTrue("first address should be accepted");
        filter.TryAccept(ip2).Should().BeTrue("second address should be accepted");
        filter.TryAccept(ip3).Should().BeTrue("third address should be accepted");

        bool ip1Accepted = filter.TryAccept(ip1);
        bool ip2Accepted = filter.TryAccept(ip2);

        (ip1Accepted || ip2Accepted).Should().BeTrue("at least one of the first two addresses should be evicted and accepted again");
    }

    [Test]
    public void AcceptAll_AlwaysReturnsTrue()
    {
        NodeFilter filter = NodeFilter.AcceptAll;
        IPAddress ip = IPAddress.Parse("192.0.2.1");

        filter.TryAccept(ip).Should().BeTrue();
        filter.TryAccept(ip).Should().BeTrue("AcceptAll should always accept");
    }

    [Test]
    public void Create_WhenDisabled_ReturnsAcceptAll()
    {
        NodeFilter filter = NodeFilter.Create(50, filterEnabled: false, subnetBucketing: true, currentIp: null);
        filter.Should().BeSameAs(NodeFilter.AcceptAll);
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

    [TestCase("192.0.2.1", "192.0.2.50", true, Description = "Same /24 IPv4")]
    [TestCase("192.0.2.1", "192.0.3.1", false, Description = "Different /24 IPv4")]
    [TestCase("2001:db8::1", "2001:db8::ffff", true, Description = "Same /64 IPv6")]
    [TestCase("2001:db8::1", "2001:db8:0:1::1", false, Description = "Different /64 IPv6")]
    public void IpSubnetKey_AreInSameSubnet(string a, string b, bool expected)
    {
        NodeFilter.IpSubnetKey.AreInSameSubnet(
            IPAddress.Parse(a), IPAddress.Parse(b),
            v4PrefixBits: 24, v6PrefixBits: 64).Should().Be(expected);
    }

    [TestCase("127.0.0.1", true, Description = "IPv4 loopback")]
    [TestCase("::1", true, Description = "IPv6 loopback")]
    [TestCase("10.0.0.1", true, Description = "RFC1918 10.x")]
    [TestCase("172.16.0.1", true, Description = "RFC1918 172.16.x")]
    [TestCase("192.168.1.1", true, Description = "RFC1918 192.168.x")]
    [TestCase("169.254.0.1", true, Description = "IPv4 link-local")]
    [TestCase("100.64.0.1", true, Description = "CGNAT")]
    [TestCase("fc00::1", true, Description = "ULA fc00")]
    [TestCase("fd00::1", true, Description = "ULA fd00")]
    [TestCase("fe80::1", true, Description = "IPv6 link-local")]
    [TestCase("8.8.8.8", false, Description = "Public IPv4")]
    [TestCase("2001:4860:4860::8888", false, Description = "Public IPv6")]
    public void IpSubnetKey_IsLoopbackOrPrivateOrLinkLocal(string address, bool expected)
    {
        NodeFilter.IpSubnetKey.IsLoopbackOrPrivateOrLinkLocal(IPAddress.Parse(address)).Should().Be(expected);
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

    [Test]
    public async Task TimeoutExpiry_ReacceptsAddressAfterTimeout()
    {
        // Use a short timeout so we can test expiry without waiting minutes
        NodeFilter filter = new(size: 100, exactMatchOnly: true, currentIp: null, timeoutMs: 100);
        IPAddress ip = IPAddress.Parse("192.0.2.1");

        filter.TryAccept(ip).Should().BeTrue("first attempt should be accepted");
        filter.TryAccept(ip).Should().BeFalse("second attempt within timeout should be rejected");

        await Task.Delay(500);

        filter.TryAccept(ip).Should().BeTrue("attempt after timeout expiry should be accepted again");
    }

    [Test]
    public async Task TimeoutExpiry_SubnetBucketing_ReacceptsAfterTimeout()
    {
        NodeFilter filter = new(size: 100, exactMatchOnly: false, currentIp: null, timeoutMs: 100);
        IPAddress ip1 = IPAddress.Parse("203.0.113.1");
        IPAddress ip2 = IPAddress.Parse("203.0.113.50");

        filter.TryAccept(ip1).Should().BeTrue("first address should be accepted");
        filter.TryAccept(ip2).Should().BeFalse("same subnet should be rejected within timeout");

        await Task.Delay(500);

        filter.TryAccept(ip2).Should().BeTrue("same subnet should be accepted after timeout expiry");
    }
}
