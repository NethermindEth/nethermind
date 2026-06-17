using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Nethermind.Core;
using Nethermind.Crypto;
using NUnit.Framework;

namespace Nethermind.IntegrationTests;

/// <summary>
/// End-to-end tests for the <c>tools/EngineApiProxy</c> tool driven by a <em>live</em> consensus
/// client. A real Lighthouse beacon node + validator client run a single-node post-merge devnet and
/// point their execution endpoint at the proxy, which sits in front of a Nethermind EL. Beyond
/// proving the loop turns, these tests validate the proxy's Lighthouse-mode flow: intercepting
/// FCU-with-payload-attributes, tracking the payloadId, running the synthetic validation cycle
/// against the EL, and forwarding VALID responses back to the CL — plus holistic checks (EL/CL head
/// agreement, transaction inclusion, and a 4844 blob transaction through the proxy).
/// </summary>
/// <remarks>
/// The devnet is brought up once in <see cref="OneTimeSetUpAsync"/> (genesis-generator → Nethermind
/// EL → EngineApiProxy in Lighthouse mode → Lighthouse beacon node with <c>--always-prepare-payload</c>
/// → validator client) and waits until the EL head advances a few blocks, guaranteeing the per-slot
/// proxy log lines the flow tests assert on already exist. The fixture is resource-heavy and runs
/// serially. The proxy runs at <c>--log-level Debug</c>, so its stdout carries the full request flow.
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

    // Well-known Hardhat/Anvil test keypairs, premined in genesis so the tx tests can sign locally
    // without deriving keys from the devnet mnemonic. Account 0 sends the legacy tx, account 1 the
    // blob tx — distinct senders keep the two tx tests order-independent over the shared devnet.
    private const string LegacyTxPrivateKey = "0xac0974bec39a17e36ba4a6b4d238ff944bacb478cbed5efcae784d7bf4f2ff80";
    private const string LegacyTxAddress = "0xf39Fd6e51aad88F6F4ce6aB8827279cffFb92266";
    private const string BlobTxPrivateKey = "0x59c6995e998f97a5a0044966f0945389dc9e86dae88c7a8412f4603b6b78690d";
    private const string BlobTxAddress = "0x70997970C51812dc3A010C7d01b50e0d17dc79C8";
    private const string RecipientAddress = "0x00000000000000000000000000000000000000Ff";

    private Utils.DevnetGenesis _genesis;
    private INetwork _network;
    private IContainer _nethermindContainer;
    private IContainer _proxyContainer;
    private IContainer _beaconContainer;
    private IContainer _validatorContainer;
    private HttpClient _elClient;
    private HttpClient _beaconClient;
    private long _headAtSetup;

    [OneTimeSetUp]
    public async Task OneTimeSetUpAsync()
    {
        _genesis = await Utils.GenerateDevnetGenesisAsync(
            validatorCount: 4,
            premine:
            [
                (LegacyTxAddress, "1000000ETH"),
                (BlobTxAddress, "1000000ETH"),
            ]);

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

        _elClient = new HttpClient
        {
            BaseAddress = new Uri($"http://{_nethermindContainer.Hostname}:{_nethermindContainer.GetMappedPublicPort(8545)}")
        };
        _beaconClient = new HttpClient
        {
            BaseAddress = new Uri($"http://{_beaconContainer.Hostname}:{_beaconContainer.GetMappedPublicPort(BeaconHttpPort)}")
        };
        _beaconClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // Wait for several building slots so every per-slot proxy log line the flow tests assert on
        // is guaranteed to exist before any test runs.
        _headAtSetup = await Utils.WaitForElBlockNumberAsync(_elClient, minBlockNumber: 3, timeout: TimeSpan.FromMinutes(4));
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDownAsync()
    {
        _elClient?.Dispose();
        _beaconClient?.Dispose();

        // Dispose CL first (depends on EL+proxy), then proxy, then EL, then the network.
        if (_validatorContainer is not null)
        {
            await _validatorContainer.DisposeAsync();
        }
        if (_beaconContainer is not null)
        {
            await _beaconContainer.DisposeAsync();
        }
        if (_proxyContainer is not null)
        {
            await _proxyContainer.DisposeAsync();
        }
        if (_nethermindContainer is not null)
        {
            await _nethermindContainer.DisposeAsync();
        }
        if (_network is not null)
        {
            await _network.DisposeAsync();
        }

        if (_genesis is not null)
        {
            // The generator/keygen containers write root-owned files into this temp tree, so a full
            // recursive delete may be denied. Best-effort cleanup; leaking the temp dir is acceptable.
            try
            {
                Directory.Delete(_genesis.RootDir, recursive: true);
            }
            catch (Exception ex)
            {
                TestContext.Progress.WriteLine($"cleanup of devnet genesis dir failed: {ex.Message}");
            }
        }
    }

    [Test]
    public async Task Lighthouse_DrivesBlockProduction_ThroughProxy()
    {
        Assert.That(_headAtSetup, Is.GreaterThanOrEqualTo(3),
            "live Lighthouse should drive at least three blocks through the proxy onto the EL");

        // The chain must keep advancing (liveness), not stall after setup.
        long head = await Utils.WaitForElBlockNumberAsync(_elClient, _headAtSetup + 1, TimeSpan.FromMinutes(1));
        Assert.That(head, Is.GreaterThan(_headAtSetup), "block production should continue past the head observed at setup");
    }

    [Test]
    public async Task Proxy_InterceptsForkChoiceUpdatedWithPayloadAttributes()
    {
        string log = await _proxyContainer.GetCleanStdoutAsync();
        Assert.Multiple(() =>
        {
            Assert.That(log, Does.Contain("LH mode: got FCU request with payload attributes"),
                "proxy should intercept Lighthouse FCU-with-payload-attributes in Lighthouse mode");
            Assert.That(log, Does.Contain("LH mode: New unique FCU request with payload attributes"),
                "proxy should treat each building-slot FCU as a new unique request");
        });
    }

    [Test]
    public async Task Proxy_TracksPayloadIdForBuildingSlots()
    {
        string log = await _proxyContainer.GetCleanStdoutAsync();
        Assert.Multiple(() =>
        {
            Assert.That(log, Does.Contain("Storing parentBeaconBlockRoot"),
                "proxy should record the parentBeaconBlockRoot from the FCU payload attributes");
            Assert.That(log, Does.Contain("with payloadId"),
                "proxy should track the payloadId the EL returns for a building slot");
        });
    }

    [Test]
    public async Task Proxy_RunsSyntheticValidationCycleAgainstExecutionClient()
    {
        string log = await _proxyContainer.GetCleanStdoutAsync();
        Assert.Multiple(() =>
        {
            Assert.That(log, Does.Contain("Lighthouse validation for block with hash:"),
                "proxy should start the Lighthouse-mode validation flow on newPayload");
            Assert.That(log, Does.Contain("Getting payload for payloadId"),
                "proxy should issue a synthetic getPayload to the EL as part of the validation cycle");
            // The "|V|" marker distinguishes the proxy's synthetic validation requests to the EL
            // from the requests it merely forwards on behalf of the CL.
            Assert.That(log, Does.Contain("PR -> EL|engine_newPayloadV3|V|"),
                "proxy should send a synthetic newPayload to the EL during validation");
        });
    }

    [Test]
    public async Task Proxy_ForwardsRequestsAndValidResponsesBetweenLayers()
    {
        string log = await _proxyContainer.GetCleanStdoutAsync();
        Assert.Multiple(() =>
        {
            Assert.That(log, Does.Contain("CL -> PR|"), "proxy should log requests received from the consensus client");
            Assert.That(log, Does.Contain("PR -> EL|engine_newPayloadV3"), "proxy should forward newPayload to the execution client");
            Assert.That(log, Does.Contain("PR -> CL|engine_newPayloadV3"), "proxy should return the newPayload response to the consensus client");
            Assert.That(log, Does.Contain("\"status\":\"VALID\""), "the execution client should accept the Lighthouse-produced payloads as VALID");
        });
    }

    [Test]
    public async Task ExecutionAndConsensusLayers_AgreeOnCanonicalHead()
    {
        using HttpResponseMessage response = await _beaconClient.GetAsync("/eth/v2/beacon/blocks/head");
        response.EnsureSuccessStatusCode();
        JsonNode block = JsonNode.Parse(await response.Content.ReadAsStringAsync());

        JsonNode executionPayload = block?["data"]?["message"]?["body"]?["execution_payload"];
        Assert.That(executionPayload, Is.Not.Null, "beacon head block should carry an execution payload (post-merge)");

        string executionBlockHash = executionPayload["block_hash"].GetValue<string>();
        long clExecutionBlockNumber = long.Parse(executionPayload["block_number"].GetValue<string>());

        // The EL must have imported the exact execution block the CL considers canonical.
        JsonNode elBlock = await Utils.SendJsonRpcRequestAsync(_elClient, "eth_getBlockByHash", executionBlockHash, false);
        Assert.That(elBlock, Is.Not.Null, "EL should know the execution block referenced by the consensus head");

        long elBlockNumber = Convert.ToInt64(elBlock["number"].GetValue<string>()[2..], 16);
        Assert.That(elBlockNumber, Is.EqualTo(clExecutionBlockNumber),
            "the EL block matching the CL head hash must have the same block number");
    }

    [Test]
    public async Task Lighthouse_IncludesLegacyTransaction_InBlockThroughProxy()
    {
        using PrivateKey signer = new(LegacyTxPrivateKey);
        Transaction tx = new()
        {
            Type = TxType.Legacy,
            Nonce = 0,
            To = new Address(RecipientAddress),
            Value = 1,
            GasLimit = 21000,
            GasPrice = 100_000_000_000UL, // 100 gwei, comfortably above base fee
        };

        string txHash = await Utils.SignAndSendTransactionAsync(_elClient, signer, tx, Utils.DevnetChainId);

        JsonNode receipt = await Utils.WaitForReceiptAsync(_elClient, txHash, TimeSpan.FromMinutes(2));
        Assert.That(receipt, Is.Not.Null, "the legacy transaction should be mined into a Lighthouse-produced block");
        Assert.Multiple(() =>
        {
            Assert.That(receipt["status"].GetValue<string>(), Is.EqualTo("0x1"), "transaction should succeed");
            Assert.That(Convert.ToInt64(receipt["blockNumber"].GetValue<string>()[2..], 16), Is.GreaterThan(0),
                "transaction should be included in a non-genesis block");
        });
    }

    [Test]
    public async Task Lighthouse_IncludesBlobTransaction_InBlockThroughProxy()
    {
        using PrivateKey signer = new(BlobTxPrivateKey);

        string txHash = await Utils.SendBlobTransactionAsync(
            _elClient, signer, Utils.DevnetChainId, new Address(RecipientAddress), blobCount: 1);

        JsonNode receipt = await Utils.WaitForReceiptAsync(_elClient, txHash, TimeSpan.FromMinutes(2));
        Assert.That(receipt, Is.Not.Null, "the blob transaction should be mined into a Lighthouse-produced block");
        Assert.That(receipt["status"].GetValue<string>(), Is.EqualTo("0x1"), "blob transaction should succeed");

        // The including block must actually carry blob gas, confirming the 4844 payload flowed
        // through the proxy into a Lighthouse-built block.
        string blockHash = receipt["blockHash"].GetValue<string>();
        JsonNode block = await Utils.SendJsonRpcRequestAsync(_elClient, "eth_getBlockByHash", blockHash, false);
        long blobGasUsed = Convert.ToInt64(block["blobGasUsed"].GetValue<string>()[2..], 16);
        Assert.That(blobGasUsed, Is.GreaterThan(0), "the including block should report non-zero blobGasUsed");
    }
}
