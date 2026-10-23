// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;

namespace Nethermind.Logging.NLog.Test;

[TestFixture]
[NonParallelizable]
public class IpMaskerTests
{
    private const string NodeKey = "a3435a0155a3e837c02f5e7f5662a2f1fbc25b48e4dc232016e1c51b0e2a6d7d32f2e4c2b6f3d6e5f9b8a7c6d5e4f3a2b1c0d9e8f7a6b5c4d3e2f1a0b9c8d7e6";

    [TestCase("Connected to 94.130.12.34:30303", "Connected to x.x.x.x:30303")]
    [TestCase("Peer 94.130.12.34.", "Peer x.x.x.x.")]
    [TestCase("tcp://94.130.12.34:30303 ", "tcp://x.x.x.x:30303 ")]
    [TestCase("10.0.0.1 and 192.168.1.20", "x.x.x.x and x.x.x.x")]
    [TestCase("[Node|94.130.12.34:30303|eth68|Geth]", "[Node|x.x.x.x:30303|eth68|Geth]")]
    [TestCase("Peer 2001:db8::7334 left", "Peer x::x left")]
    [TestCase("Peer [2001:db8:85a3::8a2e:370:7334]:30303", "Peer [x::x]:30303")]
    [TestCase("Peer fe80::1%12 left", "Peer x::x left")]
    [TestCase("Peer fe80::1: timeout", "Peer x::x: timeout")]
    [TestCase("Peer ::ffff:94.130.12.34 left", "Peer x::x left")]
    [TestCase("This node : enode://" + NodeKey + "@94.130.12.34:30303 ", "This node : enode://***@***:30303 ")]
    [TestCase("enode://" + NodeKey + "@[2001:db8::1]:30303?discport=30301", "enode://***@***:30303?discport=30301")]
    [TestCase("enode://" + NodeKey + "@node.example.com:30303|0x1234", "enode://***@***:30303|0x1234")]
    [TestCase("Bootnode enr:-IS4QHCYrYZbAKWCBRlAy5zzaDZXJBGkcnh4MHcBFZntXNFrdvJjX04jRzjzCBOonrkTfj499SZuOh8R33Ls8RRcy5wBgmlkgnY0gmlwhH8AAAGJc2VjcDI1NmsxoQPKY0yuDUmstAHYpMa2_oxVtw0RW_QAdpzBQA8yWM0xOIN1ZHCCdl8 found", "Bootnode enr:*** found")]
    public void Masks_sensitive_data(string text, string expected) =>
        Assert.That(IpMasker.Mask(text), Is.EqualTo(expected));

    // Loopback and unspecified addresses do not identify a node and help to diagnose local setups.
    [TestCase("JSON RPC listening on 127.0.0.1:8545")]
    [TestCase("Listening on 0.0.0.0:30303")]
    [TestCase("Listening on [::1]:8545")]
    [TestCase("Listening on [::]:30303")]
    [TestCase("Peer ::ffff:127.0.0.1")]
    // Versions and numbers that only look like addresses.
    [TestCase("Version: 1.36.0-unstable+3c7c9e40")]
    [TestCase("Geth/v1.14.12-stable/linux-amd64/go1.23.4")]
    [TestCase("Client v1.2.3.4")]
    [TestCase("Version 1.2.3.4.5")]
    [TestCase("Value 300.1.1.1 is not an address")]
    [TestCase("Processed 21,000,000 | 1.25 MGas | 0.5 s")]
    // Times and hashes contain colons and hex digits, but are not addresses.
    [TestCase("Took 12:34:56.789")]
    [TestCase("Block 0xd4e56740f876aef8c010b86a40d5f56745a118d0906a34e69aec8c0db1cb8fa3")]
    [TestCase("Block 0xd4e567...1cb8fa3 dead:beef:cafe")]
    [TestCase("No sensitive data")]
    public void Keeps_data_that_is_not_sensitive(string text) =>
        Assert.That(IpMasker.Mask(text), Is.EqualTo(text));

    [TestCase(true, "Peer x.x.x.x:30303")]
    [TestCase(false, "Peer 94.130.12.34:30303")]
    public void Logger_masks_only_when_enabled(bool enabled, string expected)
    {
        LoggingConfiguration previous = LogManager.Configuration;
        bool wasEnabled = IpMasker.Enabled;
        MemoryTarget target = new() { Layout = "${message}" };
        LoggingConfiguration configuration = new();
        configuration.AddRuleForAllLevels(target);
        LogManager.Configuration = configuration;
        try
        {
            IpMasker.Enabled = enabled;
            new NLogLogger(nameof(Logger_masks_only_when_enabled)).Info("Peer 94.130.12.34:30303");

            Assert.That(target.Logs, Is.EqualTo(new[] { expected }));
        }
        finally
        {
            IpMasker.Enabled = wasEnabled;
            LogManager.Configuration = previous;
        }
    }
}
