// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Network.Enr;
using NUnit.Framework;

namespace Nethermind.Bootnode.Test;

[TestFixture]
public class BootnodeNodeRecordProviderTests
{
    [Test]
    public async Task Discovery_only_node_record_omits_tcp_endpoint()
    {
        using PrivateKeyGenerator generator = new();
        using PrivateKey privateKey = generator.Generate();
        IProtectedPrivateKey protectedPrivateKey = new ProtectedPrivateKey(privateKey, TestContext.CurrentContext.WorkDirectory);
        NetworkConfig networkConfig = new()
        {
            DiscoveryPort = 30303,
            P2PPort = 0
        };
        BootnodeNodeRecordProvider provider = new(
            protectedPrivateKey,
            new StaticIpResolver(IPAddress.Loopback),
            new EthereumEcdsa(1),
            networkConfig,
            LimboLogs.Instance,
            TestContext.CurrentContext.WorkDirectory);

        NodeRecord nodeRecord = await provider.GetCurrentAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(nodeRecord.DiscoveryPort, Is.EqualTo(30303));
            Assert.That(nodeRecord.TcpPort, Is.Null);
        }
    }

    [Test]
    public async Task Corrupt_enr_sequence_state_does_not_block_identity_creation()
    {
        string dataDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        await File.WriteAllTextAsync(Path.Combine(dataDir, "enr-state.json"), "{");

        using PrivateKeyGenerator generator = new();
        using PrivateKey privateKey = generator.Generate();
        IProtectedPrivateKey protectedPrivateKey = new ProtectedPrivateKey(privateKey, dataDir);
        NetworkConfig networkConfig = new()
        {
            DiscoveryPort = 30303,
            P2PPort = 0
        };

        NodeRecord nodeRecord = await CreateProvider(protectedPrivateKey, dataDir, networkConfig, IPAddress.Loopback).GetCurrentAsync();
        string stateText = await File.ReadAllTextAsync(Path.Combine(dataDir, "enr-state.json"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(nodeRecord.EnrSequence, Is.EqualTo(1));
            Assert.That(stateText, Does.Contain(nameof(BootnodeIdentity.EnrSequence)));
        }
    }

    [Test]
    public async Task Enr_sequence_is_reused_until_record_content_changes()
    {
        string dataDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);

        using PrivateKeyGenerator generator = new();
        using PrivateKey privateKey = generator.Generate();
        IProtectedPrivateKey protectedPrivateKey = new ProtectedPrivateKey(privateKey, dataDir);
        NetworkConfig networkConfig = new()
        {
            DiscoveryPort = 30303,
            P2PPort = 0
        };

        NodeRecord firstRecord = await CreateProvider(protectedPrivateKey, dataDir, networkConfig, IPAddress.Loopback).GetCurrentAsync();
        NodeRecord sameRecord = await CreateProvider(protectedPrivateKey, dataDir, networkConfig, IPAddress.Loopback).GetCurrentAsync();
        NodeRecord changedRecord = await CreateProvider(protectedPrivateKey, dataDir, networkConfig, IPAddress.Parse("127.0.0.2")).GetCurrentAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstRecord.EnrSequence, Is.EqualTo(1));
            Assert.That(sameRecord.EnrSequence, Is.EqualTo(firstRecord.EnrSequence));
            Assert.That(changedRecord.EnrSequence, Is.EqualTo(firstRecord.EnrSequence + 1));
        }
    }

    private static BootnodeNodeRecordProvider CreateProvider(
        IProtectedPrivateKey protectedPrivateKey,
        string dataDir,
        INetworkConfig networkConfig,
        IPAddress ipAddress) =>
        new(
            protectedPrivateKey,
            new StaticIpResolver(ipAddress),
            new EthereumEcdsa(1),
            networkConfig,
            LimboLogs.Instance,
            dataDir);

    private sealed class StaticIpResolver(IPAddress address) : IIPResolver
    {
        public ValueTask<IIPResolver.NethermindIp> Resolve(CancellationToken cancellationToken = default) =>
            new(new IIPResolver.NethermindIp(address, address));
    }
}
