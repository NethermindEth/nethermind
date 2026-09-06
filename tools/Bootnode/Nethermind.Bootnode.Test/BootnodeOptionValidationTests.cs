// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net.Sockets;
using NUnit.Framework;

namespace Nethermind.Bootnode.Test;

[TestFixture]
public class BootnodeOptionValidationTests
{
    [TestCase(false, "bootnode-data", null, "127.0.0.1")]
    [TestCase(true, "data", "0.0.0.0", "0.0.0.0")]
    public void Defaults_match_execution_environment(bool isRunningInContainer, string dataDirName, string? localIp, string serviceHost)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Path.GetFileName(BootnodeOptionDefaults.DataDir(isRunningInContainer)), Is.EqualTo(dataDirName));
            Assert.That(BootnodeOptionDefaults.LocalIp(isRunningInContainer), Is.EqualTo(localIp));
            Assert.That(BootnodeOptionDefaults.ServiceHost(isRunningInContainer), Is.EqualTo(serviceHost));
        }
    }

    [TestCase(1)]
    [TestCase(65535)]
    public void Port_range_accepts_valid_ports(int port) =>
        Assert.DoesNotThrow(() => BootnodeOptionValidation.ValidatePort("--port", port));

    [TestCase(0)]
    [TestCase(65536)]
    public void Port_range_rejects_invalid_ports(int port) =>
        Assert.That(
            () => BootnodeOptionValidation.ValidatePort("--port", port),
            Throws.TypeOf<ArgumentOutOfRangeException>());

    [TestCase("--bucket-size")]
    [TestCase("--concurrency")]
    [TestCase("--discovery-interval-ms")]
    public void Positive_options_reject_zero(string optionName) =>
        Assert.That(
            () => BootnodeOptionValidation.ValidatePositive(optionName, 0),
            Throws.TypeOf<ArgumentOutOfRangeException>());

    [Test]
    public void Active_discovery_jobs_rejects_negative_values() =>
        Assert.That(
            () => BootnodeOptionValidation.ValidateNonNegative("--active-discovery-jobs", -1),
            Throws.TypeOf<ArgumentOutOfRangeException>());

    [TestCase("Trace")]
    [TestCase("Debug")]
    [TestCase("Info")]
    [TestCase("Warn")]
    [TestCase("Error")]
    public void Log_level_accepts_supported_nlog_levels(string logLevel) =>
        Assert.DoesNotThrow(() => BootnodeOptionValidation.ValidateLogLevel("--log-level", logLevel));

    [TestCase("")]
    [TestCase("Verbose")]
    public void Log_level_rejects_invalid_values(string logLevel) =>
        Assert.That(
            () => BootnodeOptionValidation.ValidateLogLevel("--log-level", logLevel),
            Throws.TypeOf<ArgumentException>());

    [TestCase("192.0.2.1", AddressFamily.InterNetwork)]
    [TestCase("::ffff:192.0.2.1", AddressFamily.InterNetwork)]
    [TestCase("2001:db8::1", AddressFamily.InterNetworkV6)]
    public void External_ip_accepts_expected_address_family(string value, AddressFamily expectedFamily) =>
        Assert.DoesNotThrow(() => BootnodeOptionValidation.ValidateExternalIp("--external-ip", value, expectedFamily));

    [TestCase("192.0.2.1")]
    [TestCase("2001:db8::1")]
    public void External_ip_without_expected_family_accepts_ipv4_and_ipv6(string value) =>
        Assert.DoesNotThrow(() => BootnodeOptionValidation.ValidateExternalIp("--external-ip", value, expectedFamily: null));

    [Test]
    public void Omitted_external_ip_is_valid() =>
        Assert.DoesNotThrow(() => BootnodeOptionValidation.ValidateExternalIp("--external-ip", value: null, expectedFamily: null));

    [TestCase("", AddressFamily.InterNetwork)]
    [TestCase("203.0.113", AddressFamily.InterNetwork)]
    [TestCase("0.0.0.0", AddressFamily.InterNetwork)]
    [TestCase("::ffff:0.0.0.0", AddressFamily.InterNetwork)]
    [TestCase("::ffff:255.255.255.255", AddressFamily.InterNetwork)]
    [TestCase("2001:db8::1", AddressFamily.InterNetwork)]
    [TestCase("::ffff:192.0.2.1", AddressFamily.InterNetworkV6)]
    public void External_ip_rejects_invalid_or_unusable_values(string value, AddressFamily expectedFamily) =>
        Assert.That(
            () => BootnodeOptionValidation.ValidateExternalIp("--external-ip", value, expectedFamily),
            Throws.TypeOf<ArgumentException>());

    [TestCase("127.0.0.1")]
    [TestCase("::")]
    [TestCase("localhost")]
    [TestCase("*")]
    [TestCase("+")]
    public void Host_accepts_kestrel_listener_names(string value) =>
        Assert.DoesNotThrow(() => BootnodeOptionValidation.ValidateHost("--http-host", value));

    [TestCase("")]
    [TestCase("bad host")]
    [TestCase("http://localhost")]
    [TestCase("localhost:8546")]
    public void Host_rejects_invalid_listener_names(string value) =>
        Assert.That(
            () => BootnodeOptionValidation.ValidateHost("--http-host", value),
            Throws.TypeOf<ArgumentException>());
}
