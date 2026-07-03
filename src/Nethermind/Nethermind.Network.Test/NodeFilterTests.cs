// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
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

        Assert.That(filter.TryAccept(ip), Is.True, "first attempt should be accepted");
        Assert.That(filter.TryAccept(ip), Is.False, "second attempt within timeout should be rejected");
    }

    [Test]
    public void ExactMatch_AllowsDifferentAddresses()
    {
        NodeFilter filter = CreateFilter(exactMatchOnly: true);
        IPAddress ip1 = IPAddress.Parse("192.0.2.1");
        IPAddress ip2 = IPAddress.Parse("192.0.2.2");

        Assert.That(filter.TryAccept(ip1), Is.True, "first address should be accepted");
        Assert.That(filter.TryAccept(ip2), Is.True, "different address should be accepted");
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
        Assert.That(filter.TryAccept(IPAddress.Parse(first)), Is.True, "first address should be accepted");
        Assert.That(filter.TryAccept(IPAddress.Parse(second)), Is.EqualTo(secondAccepted));
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

        Assert.That(acceptedCount, Is.EqualTo(threadCount * attemptsPerThread), "all unique addresses should be accepted");
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
        Assert.That(acceptedCount, Is.GreaterThan(0), "at least one concurrent attempt should be accepted");
        Assert.That(acceptedCount, Is.LessThan(threadCount * attemptsPerThread), "not all concurrent attempts should be accepted for the same address");
    }

    [Test]
    public void CapacityBounded_EvictsOldEntries()
    {
        NodeFilter filter = CreateFilter(size: 2, exactMatchOnly: true);
        IPAddress ip1 = IPAddress.Parse("192.0.2.1");
        IPAddress ip2 = IPAddress.Parse("192.0.2.2");
        IPAddress ip3 = IPAddress.Parse("192.0.2.3");

        Assert.That(filter.TryAccept(ip1), Is.True, "first address should be accepted");
        Assert.That(filter.TryAccept(ip2), Is.True, "second address should be accepted");
        Assert.That(filter.TryAccept(ip3), Is.True, "third address should be accepted");

        bool ip1Accepted = filter.TryAccept(ip1);
        bool ip2Accepted = filter.TryAccept(ip2);

        Assert.That((ip1Accepted || ip2Accepted), Is.True, "at least one of the first two addresses should be evicted and accepted again");
    }

    [Test]
    public void AcceptAll_AlwaysReturnsTrue()
    {
        NodeFilter filter = NodeFilter.AcceptAll;
        IPAddress ip = IPAddress.Parse("192.0.2.1");

        Assert.That(filter.TryAccept(ip), Is.True);
        Assert.That(filter.TryAccept(ip), Is.True, "AcceptAll should always accept");
    }

    [Test]
    public void Create_WhenDisabled_ReturnsAcceptAll()
    {
        NodeFilter filter = NodeFilter.Create(50, filterEnabled: false, subnetBucketing: true, currentIp: null);
        Assert.That(filter, Is.SameAs(NodeFilter.AcceptAll));
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
    public void IPAddressExtensions_IsLoopbackOrPrivateOrLinkLocal(string address, bool expected) => Assert.That(IPAddress.Parse(address).IsLoopbackOrPrivateOrLinkLocal, Is.EqualTo(expected));

    [TestCase("0.1.2.3", true, Description = "IPv4 this-network")]
    [TestCase("192.0.0.1", true, Description = "IPv4 IETF protocol assignments")]
    [TestCase("192.0.2.1", true, Description = "IPv4 documentation TEST-NET-1")]
    [TestCase("192.31.196.1", true, Description = "IPv4 AS112")]
    [TestCase("192.52.193.1", true, Description = "IPv4 AMT")]
    [TestCase("198.18.0.1", true, Description = "IPv4 benchmarking")]
    [TestCase("192.175.48.1", true, Description = "IPv4 direct delegation AS112")]
    [TestCase("198.51.100.1", true, Description = "IPv4 documentation TEST-NET-2")]
    [TestCase("203.0.113.1", true, Description = "IPv4 documentation TEST-NET-3")]
    [TestCase("224.0.0.1", true, Description = "IPv4 multicast")]
    [TestCase("240.0.0.1", true, Description = "IPv4 reserved")]
    [TestCase("::ffff:224.0.0.1", true, Description = "IPv4-mapped multicast")]
    [TestCase("64:ff9b::1", true, Description = "IPv6 IPv4/IPv6 translation")]
    [TestCase("64:ff9b:1::1", true, Description = "IPv6 local-use translation")]
    [TestCase("100::1", true, Description = "IPv6 discard-only")]
    [TestCase("2001::1", true, Description = "IPv6 IETF protocol assignments")]
    [TestCase("2001:db8::1", true, Description = "IPv6 documentation")]
    [TestCase("2002::1", true, Description = "IPv6 6to4")]
    [TestCase("3fff::1", true, Description = "IPv6 documentation")]
    [TestCase("8.8.8.8", false, Description = "Public IPv4")]
    [TestCase("2001:4860:4860::8888", false, Description = "Public IPv6")]
    public void IPAddressExtensions_IsSpecialUseAddress(string address, bool expected) => Assert.That(IPAddress.Parse(address).IsSpecialUseAddress, Is.EqualTo(expected));

    [TestCase("224.0.0.1", true, Description = "IPv4 multicast")]
    [TestCase("ff02::1", true, Description = "IPv6 multicast")]
    [TestCase("8.8.8.8", false, Description = "Public IPv4")]
    [TestCase("2001:4860:4860::8888", false, Description = "Public IPv6")]
    public void IPAddressExtensions_IsMulticast(string address, bool expected) => Assert.That(IPAddress.Parse(address).IsMulticast, Is.EqualTo(expected));

    [TestCase("192.168.1.10", "192.168.1.20", "203.0.113.1", false, Description = "Private addresses use exact keying")]
    [TestCase("203.0.113.1", "203.0.113.50", "198.51.100.1", true, Description = "Public addresses in same /24 use subnet bucketing")]
    [TestCase("192.168.1.10", "192.168.1.20", "192.168.1.1", false, Description = "Same local subnet uses exact keying")]
    public void TryAccept_UsesExpectedKeyingBehavior(string remote1, string remote2, string current, bool expectEqual)
    {
        NodeFilter filter = CreateFilter(currentIp: IPAddress.Parse(current));

        if (expectEqual)
        {
            Assert.That(filter.TryAccept(IPAddress.Parse(remote1)), Is.True);
            Assert.That(filter.TryAccept(IPAddress.Parse(remote2)), Is.False);
        }
        else
        {
            Assert.That(filter.TryAccept(IPAddress.Parse(remote1)), Is.True);
            Assert.That(filter.TryAccept(IPAddress.Parse(remote2)), Is.True);
        }
    }

    [TestCase(true, "192.0.2.1", "192.0.2.1", Description = "Exact match reaccepts after timeout")]
    [TestCase(false, "203.0.113.1", "203.0.113.50", Description = "Subnet bucket reaccepts after timeout")]
    public async Task TimeoutExpiry_ReacceptsAfterTimeout(bool exactMatchOnly, string first, string second)
    {
        NodeFilter filter = CreateFilter(exactMatchOnly: exactMatchOnly, timeoutMs: 100);
        IPAddress ip1 = IPAddress.Parse(first);
        IPAddress ip2 = IPAddress.Parse(second);

        Assert.That(filter.TryAccept(ip1), Is.True, "first attempt should be accepted");
        Assert.That(filter.TryAccept(ip2), Is.False, "second attempt within timeout should be rejected");

        await Task.Delay(500);

        Assert.That(filter.TryAccept(ip2), Is.True, "attempt after timeout expiry should be accepted again");
    }

    [TestCase(true, "192.0.2.1", "192.0.2.1", Description = "Touch refreshes exact match timeout")]
    [TestCase(false, "203.0.113.1", "203.0.113.50", Description = "Touch refreshes subnet bucket timeout")]
    public async Task Touch_RefreshesTimeout(bool exactMatchOnly, string firstAddr, string touchAddr)
    {
        NodeFilter filter = CreateFilter(exactMatchOnly: exactMatchOnly, timeoutMs: 2000);
        IPAddress ip1 = IPAddress.Parse(firstAddr);
        IPAddress ip2 = IPAddress.Parse(touchAddr);

        Assert.That(filter.TryAccept(ip1), Is.True, "first attempt should be accepted");

        await Task.Delay(500);

        filter.Touch(ip2);

        await Task.Delay(1000);

        Assert.That(filter.TryAccept(ip1), Is.False, "touch should refresh the timeout window");

        await Task.Delay(1500);

        Assert.That(filter.TryAccept(ip1), Is.True, "address should be accepted again after the refreshed timeout expires");
    }
}
