// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using NUnit.Framework;

namespace Nethermind.Network.Test;

[Parallelizable(ParallelScope.Self)]
public class NetworkHelperTests
{
    [TestCase("0.0.0.0", null, true, "::")]
    [TestCase("0.0.0.0", "0.0.0.0", true, "0.0.0.0")]
    [TestCase("0.0.0.0", "0", true, "0.0.0.0")]
    [TestCase("0.0.0.0", " 0.0.0.0 ", true, "0.0.0.0")]
    [TestCase("::", null, true, "::")]
    [TestCase("::", "::", true, "::")]
    [TestCase("127.0.0.1", null, true, "127.0.0.1")]
    [TestCase("192.168.1.5", null, true, "192.168.1.5")]
    [TestCase("192.168.1.5", "192.168.1.5", true, "192.168.1.5")]
    [TestCase("2001:db8::1", null, true, "2001:db8::1")]
    [TestCase("2001:db8::1", null, false, "2001:db8::1")]
    [TestCase("0.0.0.0", null, false, "0.0.0.0")]
    [TestCase("::", null, false, "::")]
    public void GetInboundBindAddress_honors_explicit_configuration_and_dual_stack_support(string localIp, string? localIpConfig, bool supportsDualStack, string expectedIp)
    {
        IPAddress result = NetworkHelper.GetInboundBindAddress(IPAddress.Parse(localIp), localIpConfig, supportsDualStack);

        Assert.That(result, Is.EqualTo(IPAddress.Parse(expectedIp)));
    }

    [TestCase("::ffff:192.168.1.5", "192.168.1.5")]
    [TestCase("::ffff:7f00:1", "127.0.0.1")]
    [TestCase("2001:db8::1", "2001:db8::1")]
    [TestCase("127.0.0.1", "127.0.0.1")]
    public void NormalizeMappedIPv4_reduces_mapped_addresses_to_ipv4(string input, string expectedIp)
        => Assert.That(IPAddress.Parse(input).NormalizeMappedIPv4(), Is.EqualTo(IPAddress.Parse(expectedIp)));
}
