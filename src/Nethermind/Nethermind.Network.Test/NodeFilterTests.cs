// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
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
    private static NodeFilter CreateFilter(int size = 100, bool exactMatchOnly = false,
        IPAddress? currentIp = null, long timeoutMs = 0) =>
        timeoutMs > 0
            ? new NodeFilter(size, exactMatchOnly, currentIp, timeoutMs)
            : new NodeFilter(size, exactMatchOnly, currentIp);

    [TestCase("192.0.2.1", Description = "IPv4 exact match blocks same address")]
    [TestCase("::1", Description = "IPv6 loopback treated as exact match")]
    public void ExactMatch_BlocksSameAddressWithinTimeout(string address)
    {
        NodeFilter filter = CreateFilter(exactMatchOnly: true);
        IPAddress ip = IPAddress.Parse(address);

        filter.TryAccept(ip).Should().BeTrue("first attempt should be accepted");
        filter.TryAccept(ip).Should().BeFalse("second attempt within timeout should be rejected");
    }

    [Test]
    public void ExactMatch_AllowsDifferentAddresses()
    {
        NodeFilter filter = CreateFilter(exactMatchOnly: true);
        IPAddress ip1 = IPAddress.Parse("192.0.2.1");
        IPAddress ip2 = IPAddress.Parse("192.0.2.2");

        filter.TryAccept(ip1).Should().BeTrue("first address should be accepted");
        filter.TryAccept(ip2).Should().BeTrue("different address should be accepted");
    }

    [TestCase("192.0.2.1", "192.0.2.50", false, Description = "Subnet: same /24 blocked")]
    [TestCase("192.0.2.1", "192.0.3.1", true, Description = "Subnet: different /24 allowed")]
    [TestCase("2001:db8::1", "2001:db8::ffff", false, Description = "Subnet: same /64 IPv6 blocked")]
    [TestCase("2001:db8:0:0::1", "2001:db8:0:1::1", true, Description = "Subnet: different /64 IPv6 allowed")]
    [TestCase("192.0.2.1", "::ffff:192.0.2.1", false, Description = "IPv4-mapped: treated as same IPv4")]
    [TestCase("::ffff:192.0.2.1", "::ffff:192.0.2.50", false, Description = "IPv4-mapped: same /24 blocked")]
    [TestCase("::ffff:192.0.2.1", "::ffff:192.0.3.1", true, Description = "IPv4-mapped: different /24 allowed")]
    [TestCase("10.0.0.1", "10.0.0.2", true, Description = "Private exact: RFC1918 10.x")]
    [TestCase("172.16.0.1", "172.16.0.2", true, Description = "Private exact: RFC1918 172.16.x")]
    [TestCase("192.168.1.1", "192.168.1.2", true, Description = "Private exact: RFC1918 192.168.x")]
    [TestCase("127.0.0.1", "127.0.0.2", true, Description = "Private exact: loopback")]
    [TestCase("169.254.0.1", "169.254.0.2", true, Description = "Private exact: IPv4 link-local")]
    [TestCase("100.64.0.1", "100.64.0.2", true, Description = "Private exact: CGNAT")]
    [TestCase("fc00::1", "fc00::2", true, Description = "Private exact: IPv6 ULA")]
    [TestCase("fe80::1", "fe80::2", true, Description = "Private exact: IPv6 link-local")]
    [TestCase("192.0.2.255", "192.0.3.0", true, Description = "Boundary: IPv4 /24")]
    [TestCase("2001:db8:0:0:ffff:ffff:ffff:ffff", "2001:db8:0:1::0", true, Description = "Boundary: IPv6 /64")]
    public void TryAccept_FiltersSecondAddress(string first, string second, bool secondAccepted)
    {
        NodeFilter filter = CreateFilter();
        filter.TryAccept(IPAddress.Parse(first)).Should().BeTrue("first address should be accepted");
        filter.TryAccept(IPAddress.Parse(second)).Should().Be(secondAccepted);
    }

    [Test]
    public void ThreadSafety_ConcurrentSetCalls()
    {
        NodeFilter filter = CreateFilter(size: 1000, exactMatchOnly: true);
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
        NodeFilter filter = CreateFilter(exactMatchOnly: true);
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

    [TestCase("192.0.2.1", "10.0.0.1", 0, 0, Description = "IPv4 /0 prefix - all match")]
    [TestCase("192.0.2.1", "192.0.2.1", 32, 128, Description = "IPv4 /32 prefix - exact match")]
    [TestCase("2001:db8::1", "fe80::1", 0, 0, Description = "IPv6 /0 prefix - all match")]
    [TestCase("2001:db8::1", "2001:db8::1", 32, 128, Description = "IPv6 /128 prefix - exact match")]
    public void SubnetMasking_EdgeCase_PrefixBoundary(string addr1, string addr2, byte v4Prefix, byte v6Prefix)
    {
        NodeFilter.IpSubnetKey key1 = new(IPAddress.Parse(addr1), v4PrefixBits: v4Prefix, v6PrefixBits: v6Prefix);
        NodeFilter.IpSubnetKey key2 = new(IPAddress.Parse(addr2), v4PrefixBits: v4Prefix, v6PrefixBits: v6Prefix);

        key1.Should().Be(key2);
    }

    [Test]
    public void CapacityBounded_EvictsOldEntries()
    {
        NodeFilter filter = CreateFilter(size: 2, exactMatchOnly: true);
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

    [TestCase("192.0.2.1", "192.0.2.1", true, Description = "Same address - equal")]
    [TestCase("192.0.2.1", "192.0.2.2", false, Description = "Different address - not equal")]
    public void IpSubnetKey_Equality_Exact(string addr1, string addr2, bool expectEqual)
    {
        NodeFilter.IpSubnetKey key1 = NodeFilter.IpSubnetKey.Exact(IPAddress.Parse(addr1));
        NodeFilter.IpSubnetKey key2 = NodeFilter.IpSubnetKey.Exact(IPAddress.Parse(addr2));

        if (expectEqual)
        {
            key1.Should().Be(key2);
            key1.GetHashCode().Should().Be(key2.GetHashCode());
        }
        else
        {
            key1.Should().NotBe(key2);
        }
    }

    [TestCase("192.0.2.1", "192.0.2.50", true, Description = "Same /24 subnet matches")]
    [TestCase("192.0.2.1", "192.0.3.1", false, Description = "Different /24 subnet does not match")]
    public void IpSubnetKey_Matches(string bucketAddr, string testAddr, bool expected)
    {
        NodeFilter.IpSubnetKey key = NodeFilter.IpSubnetKey.Bucket(IPAddress.Parse(bucketAddr), v4PrefixBits: 24, v6PrefixBits: 64);
        key.Matches(IPAddress.Parse(testAddr)).Should().Be(expected);
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

    [TestCase("192.168.1.10", "192.168.1.20", "203.0.113.1", false, Description = "Private addresses use exact keying")]
    [TestCase("203.0.113.1", "203.0.113.50", "198.51.100.1", true, Description = "Public addresses in same /24 use subnet bucketing")]
    [TestCase("192.168.1.10", "192.168.1.20", "192.168.1.1", false, Description = "Same local subnet uses exact keying")]
    public void CreateNodeFilterKey_KeyingBehavior(string remote1, string remote2, string current, bool expectEqual)
    {
        NodeFilter.IpSubnetKey key1 = NodeFilter.IpSubnetKey.CreateNodeFilterKey(IPAddress.Parse(remote1), IPAddress.Parse(current));
        NodeFilter.IpSubnetKey key2 = NodeFilter.IpSubnetKey.CreateNodeFilterKey(IPAddress.Parse(remote2), IPAddress.Parse(current));

        if (expectEqual)
            key1.Should().Be(key2);
        else
            key1.Should().NotBe(key2);
    }

    [TestCase(true, "192.0.2.1", "192.0.2.1", Description = "Exact match reaccepts after timeout")]
    [TestCase(false, "203.0.113.1", "203.0.113.50", Description = "Subnet bucket reaccepts after timeout")]
    public async Task TimeoutExpiry_ReacceptsAfterTimeout(bool exactMatchOnly, string first, string second)
    {
        NodeFilter filter = CreateFilter(exactMatchOnly: exactMatchOnly, timeoutMs: 100);
        IPAddress ip1 = IPAddress.Parse(first);
        IPAddress ip2 = IPAddress.Parse(second);

        filter.TryAccept(ip1).Should().BeTrue("first attempt should be accepted");
        filter.TryAccept(ip2).Should().BeFalse("second attempt within timeout should be rejected");

        await Task.Delay(500);

        filter.TryAccept(ip2).Should().BeTrue("attempt after timeout expiry should be accepted again");
    }

    [TestCase(true, "192.0.2.1", "192.0.2.1", Description = "Touch refreshes exact match timeout")]
    [TestCase(false, "203.0.113.1", "203.0.113.50", Description = "Touch refreshes subnet bucket timeout")]
    public async Task Touch_RefreshesTimeout(bool exactMatchOnly, string firstAddr, string touchAddr)
    {
        NodeFilter filter = CreateFilter(exactMatchOnly: exactMatchOnly, timeoutMs: 200);
        IPAddress ip1 = IPAddress.Parse(firstAddr);
        IPAddress ip2 = IPAddress.Parse(touchAddr);

        filter.TryAccept(ip1).Should().BeTrue("first attempt should be accepted");

        await Task.Delay(100);

        filter.Touch(ip2);

        await Task.Delay(150);

        filter.TryAccept(ip1).Should().BeFalse("touch should refresh the timeout window");

        await Task.Delay(100);

        filter.TryAccept(ip1).Should().BeTrue("address should be accepted again after the refreshed timeout expires");
    }
}
