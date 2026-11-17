// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Reflection;
using Autofac;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Specs;

namespace Nethermind.LightClient.Test;

public class ExecutionNetworkTests
{
    private const string Bootnode = "enode://81863f47e9bd652585d3f78b4b2ee07b93dad603fd9bc3c293e1244250725998adc88da0cef48f1de89b15ab92b15db8f43dc2b6fb8fbd86a6f217a1dd886701@193.70.55.37:30303";
    private const ulong ChainId = 424_242;

    private const string Genesis = """
        {
          "config": {
            "chainId": 424242, "homesteadBlock": 0, "eip150Block": 0, "eip155Block": 0, "eip158Block": 0,
            "byzantiumBlock": 0, "constantinopleBlock": 0, "petersburgBlock": 0, "istanbulBlock": 0, "berlinBlock": 0,
            "londonBlock": 0, "mergeNetsplitBlock": 0, "shanghaiTime": 0, "cancunTime": 0, "pragueTime": 0,
            "terminalTotalDifficulty": 0, "terminalTotalDifficultyPassed": true
          },
          "nonce": "0x0", "timestamp": "0x6ae23ae3", "extraData": "0x", "gasLimit": "0x1c9c380", "difficulty": "0x0",
          "mixHash": "0x0000000000000000000000000000000000000000000000000000000000000000",
          "coinbase": "0x0000000000000000000000000000000000000000", "baseFeePerGas": "0x3b9aca00",
          "alloc": { "0x00000000000000000000000000000000000000aa": { "balance": "0x1" } }
        }
        """;

    [TestCase("mainnet", "foundation.json")]
    [TestCase("hoodi", "hoodi.json")]
    [TestCase("sepolia", "sepolia.json")]
    public void Ethereum_networks_keep_known_genesis_and_dns(string name, string chainSpec)
    {
        ExecutionNetwork network = ExecutionNetwork.Ethereum(name);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(network.ChainSpecFile, Is.EqualTo(chainSpec));
            Assert.That(network.DiscoveryDns, Is.EqualTo($"enrtree://AKA3AM6LPBYEUDMVNU3BSVQJ5AD45Y7YPOHJLEF6W26QOE4VTUDPE@snap.{name}.ethdisco.net"));
            Assert.That(network.Bootnodes, Is.Empty);
            Assert.That(network.StaticPeers, Is.Null);
        }
    }

    [Test]
    public void Unknown_ethereum_network_is_rejected() =>
        Assert.That(() => ExecutionNetwork.Ethereum("rollupx"), Throws.ArgumentException);

    [TestCase("")]
    [TestCase("..")]
    [TestCase("a/b")]
    public void Custom_network_name_must_be_a_directory_name(string name) =>
        Assert.That(() => ExecutionNetwork.Custom(name, "genesis.json", Keccak.Zero, null, Bootnode), Throws.ArgumentException);

    [Test]
    public void Custom_network_needs_peer_discovery() =>
        Assert.That(() => ExecutionNetwork.Custom("rollupx", "genesis.json", Keccak.Zero, null, " , "), Throws.ArgumentException);

    [Test]
    public void Custom_network_rejects_invalid_bootnode() =>
        Assert.That(() => ExecutionNetwork.Custom("rollupx", "genesis.json", Keccak.Zero, null, "enode://bad"), Throws.Exception);

    [Test]
    public async Task Custom_network_configures_transport()
    {
        string directory = TestDirectory();
        ExecutionNetwork network = ExecutionNetwork.Custom("rollupx", WriteGenesis(directory), Keccak.Compute("rollup genesis"),
            null, $" {Bootnode} ");
        await using ExecutionPeerTransport transport = new(network, LimboLogs.Instance,
            new VerifiedHead(0, 1, Keccak.Compute("rollup head"), Keccak.Zero), 0, directory);
        IContainer services = Services(transport);
        INetworkConfig configuration = services.Resolve<INetworkConfig>();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(services.Resolve<ISpecProvider>().ChainId, Is.EqualTo(ChainId));
            Assert.That(configuration.DiscoveryDns, Is.Empty);
            Assert.That(transport.DiscoveryDomain, Is.EqualTo("<unset>"));
            Assert.That(configuration.StaticPeers, Is.EqualTo(Bootnode));
            Assert.That(configuration.Bootnodes.Select(static node => node.NodeId), Is.EqualTo(new[] { new NetworkNode(Bootnode).NodeId }));
            Assert.That(services.Resolve<IInitConfig>().DataDir, Is.EqualTo(Path.Combine(directory, "el", "rollupx")));
        }
    }

    [Test]
    public void Custom_genesis_must_match_configured_hash()
    {
        string directory = TestDirectory();
        ExecutionNetwork network = ExecutionNetwork.Custom("rollupx", WriteGenesis(directory), Keccak.Compute("other genesis"),
            null, Bootnode);

        Assert.That(() => new ExecutionPeerTransport(network, LimboLogs.Instance, dataDirectory: directory), Throws.InstanceOf<InvalidDataException>());
    }

    private static string WriteGenesis(string directory)
    {
        string path = Path.Combine(directory, "genesis.json");
        File.WriteAllText(path, Genesis);
        return path;
    }

    private static string TestDirectory()
    {
        string directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "artifacts", "NethermindLightClientNetworkTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static IContainer Services(ExecutionPeerTransport transport) => (IContainer)typeof(ExecutionPeerTransport)
        .GetField("_services", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(transport)!;
}
