using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using NUnit.Framework;

namespace Nethermind.IntegrationTests;

/// <summary>
/// End-to-end test for the <c>tools/EngineApiProxy</c> tool driven by a <em>live</em> consensus
/// client. A real Lighthouse beacon node + validator client run a single-node post-merge devnet and
/// point their execution endpoint at the proxy, which sits in front of a Nethermind EL. This
/// exercises the proxy's primary purpose — intercepting genuine CL↔EL Engine API traffic — rather
/// than the simulated FCU/newPayload sequence in <see cref="EngineApiProxyTests"/>.
/// </summary>
/// <remarks>
/// Topology on a shared Docker network: <c>genesis-generator</c> (one-shot) → Nethermind EL →
/// EngineApiProxy (Lighthouse mode) → Lighthouse beacon node (<c>--always-prepare-payload</c>) +
/// validator client. The genesis is minted at test time with genesis time anchored to "now" so the
/// chain starts producing within a few slots. The test is resource-heavy (four long-lived
/// containers plus two short-lived generators) and is therefore not parallelized.
/// </remarks>
[TestFixture]
[NonParallelizable]
public class EngineApiProxyLighthouseTests
{
    private const int ProxyPort = 9551;
    private const int BeaconHttpPort = 5052;
    private const string FeeRecipient = "0x8943545177806ed17b9f23f0a21ee5948ecaa776";
    private const string NethermindAlias = "nethermind-el";
    private const string ProxyAlias = "el-proxy";
    private const string BeaconAlias = "lighthouse-bn";

    private const string ChainspecContainerPath = "/genesis/chainspec.json";
    private const string JwtContainerPath = "/genesis/jwt.hex";

    private Utils.DevnetGenesis _genesis;
    private INetwork _network;
    private IContainer _nethermindContainer;
    private IContainer _proxyContainer;
    private IContainer _beaconContainer;
    private IContainer _validatorContainer;

    [TearDown]
    public async Task TearDown()
    {
        // Dispose CL first (depends on EL+proxy), then proxy, then EL, then the network.
        if (_validatorContainer is not null)
        {
            await _validatorContainer.DisposeAsync();
            _validatorContainer = null;
        }
        if (_beaconContainer is not null)
        {
            await _beaconContainer.DisposeAsync();
            _beaconContainer = null;
        }
        if (_proxyContainer is not null)
        {
            await _proxyContainer.DisposeAsync();
            _proxyContainer = null;
        }
        if (_nethermindContainer is not null)
        {
            await _nethermindContainer.DisposeAsync();
            _nethermindContainer = null;
        }

        if (_network is not null)
        {
            await _network.DisposeAsync();
            _network = null;
        }

        if (_genesis is not null)
        {
            // Best-effort: the generator/keygen containers create root-owned files under this temp
            // tree, so a full recursive delete may be denied. Leaking the temp dir is acceptable and
            // consistent with the other integration tests' chainspec extraction.
            try
            {
                Directory.Delete(_genesis.RootDir, recursive: true);
            }
            catch (Exception)
            {
                // ignored
            }
            _genesis = null;
        }
    }

    [Test]
    public async Task Proxy_ProducesBlocks_DrivenByLiveLighthouse_InLighthouseMode()
    {
        _genesis = await Utils.GenerateDevnetGenesisAsync(validatorCount: 4);

        _network = new NetworkBuilder().Build();
        await _network.CreateAsync();

        string[] nethermindCommand =
        {
            "--config", "sepolia",
            "--Init.ChainSpecPath", ChainspecContainerPath,
            "--JsonRpc.Enabled", "true",
            "--JsonRpc.Host", "0.0.0.0",
            "--JsonRpc.Port", "8545",
            "--JsonRpc.EnginePort", "8551",
            "--JsonRpc.EngineHost", "0.0.0.0",
            "--JsonRpc.JwtSecretFile", JwtContainerPath,
            "--JsonRpc.EngineEnabledModules", "[Engine,Eth,Web3,Net]",
            "--Merge.Enabled", "true",
            "--Sync.NetworkingEnabled", "false",
            "--Sync.SynchronizationEnabled", "false",
            "--Sync.FastSync", "false",
            "--Sync.SnapSync", "false",
        };

        (string, string)[] bindMounts =
        {
            (_genesis.ChainspecHostPath, ChainspecContainerPath),
            (_genesis.JwtHostPath, JwtContainerPath),
        };

        ContainerBuilder nethermindBuilder = (await Utils.BuildNethermindContainerAsync(nethermindCommand, bindMounts: bindMounts))
            .WithNetwork(_network)
            .WithNetworkAliases(NethermindAlias);
        _nethermindContainer = nethermindBuilder.Build();
        await _nethermindContainer.StartAsync();

        string[] proxyArgs =
        {
            "-e", $"http://{NethermindAlias}:8551",
            "-p", ProxyPort.ToString(),
            "--validation-mode", "Lighthouse",
            "--get-payload-method", "engine_getPayloadV3",
            "--new-payload-method", "engine_newPayloadV3",
            "--log-level", "Debug",
        };
        ContainerBuilder proxyBuilder = (await Utils.BuildEngineApiProxyContainerAsync(proxyArgs))
            .WithNetwork(_network)
            .WithNetworkAliases(ProxyAlias)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Engine API Proxy started on port"));
        _proxyContainer = proxyBuilder.Build();
        await _proxyContainer.StartAsync();

        _beaconContainer = Utils.BuildLighthouseBeaconContainer(
            _genesis, _network, BeaconAlias, $"http://{ProxyAlias}:{ProxyPort}", FeeRecipient).Build();
        await _beaconContainer.StartAsync();

        _validatorContainer = Utils.BuildLighthouseValidatorContainer(
            _genesis, _network, $"http://{BeaconAlias}:{BeaconHttpPort}", FeeRecipient).Build();
        await _validatorContainer.StartAsync();

        using HttpClient elClient = new()
        {
            BaseAddress = new Uri($"http://{_nethermindContainer.Hostname}:{_nethermindContainer.GetMappedPublicPort(8545)}")
        };

        long head = await Utils.WaitForElBlockNumberAsync(elClient, minBlockNumber: 2, timeout: TimeSpan.FromMinutes(4));

        Assert.That(head, Is.GreaterThanOrEqualTo(2),
            "live Lighthouse should drive at least two blocks through the proxy onto the EL");

        // Confirm the proxy actually exercised its Lighthouse-mode interception path (FCU carrying
        // payload attributes), not merely a transparent pass-through.
        string proxyLog = await _proxyContainer.GetCleanStdoutAsync();
        Assert.That(proxyLog, Does.Contain("LH mode: got FCU request with payload attributes"),
            "proxy should intercept Lighthouse FCU-with-payload-attributes in Lighthouse mode");
    }
}
