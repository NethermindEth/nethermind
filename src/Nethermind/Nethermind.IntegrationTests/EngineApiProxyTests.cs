using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using NUnit.Framework;

namespace Nethermind.IntegrationTests;

/// <summary>
/// End-to-end tests for the <c>tools/EngineApiProxy</c> tool. Each test spins up a Nethermind
/// container as the EL and an EngineApiProxy container in front of it on a shared Docker
/// network, then drives Engine API and standard JSON-RPC calls through the proxy.
/// </summary>
/// <remarks>
/// The proxy is exercised as the deployable Docker artifact (built once per process by
/// <see cref="GlobalSetup"/>), not as an in-process library, so these tests cover the same
/// surface real users see: CLI parsing, container lifecycle, header forwarding, and the
/// per-mode FCU/newPayload flows. Block-production tests use V3 (Cancun) so the proxy's
/// configured <c>--get-payload-method</c>/<c>--new-payload-method</c> match the version
/// of the payloads our test CL is producing.
/// </remarks>
[TestFixture]
[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class EngineApiProxyTests
{
    private const long SepoliaCancunTimestamp = 0x65b97d60L;
    private const int ProxyPort = 9551;
    private const string ProxyEcEndpoint = "http://nethermind-el:8551";
    private const string NethermindNetworkAlias = "nethermind-el";

    private INetwork _network;
    private IContainer _nethermindContainer;
    private IContainer _proxyContainer;

    [TearDown]
    public async Task TearDown()
    {
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
            await _network.DeleteAsync();
            _network = null;
        }
    }

    [Test]
    public async Task Proxy_Forwards_EthBlockNumber()
    {
        await StartNethermindAndProxyAsync();

        using HttpClient proxyClient = await CreateAuthenticatedProxyClientAsync();
        JsonNode result = await Utils.SendEngineRequestAsync(proxyClient, "eth_blockNumber");

        Assert.That(result.GetValue<string>(), Is.EqualTo("0x0"));
    }

    [Test]
    public async Task Proxy_Forwards_NonEngineMethod_ViaDefaultHandler()
    {
        // web3_clientVersion doesn't match any engine_* prefix, so the proxy routes through
        // DefaultRequestHandler. Verifies that arbitrary EL methods pass through untouched.
        await StartNethermindAndProxyAsync();

        using HttpClient proxyClient = await CreateAuthenticatedProxyClientAsync();
        JsonNode result = await Utils.SendEngineRequestAsync(proxyClient, "web3_clientVersion");

        Assert.That(result.GetValue<string>(), Does.StartWith("Nethermind/"));
    }

    [Test]
    public async Task Proxy_ProducesBlock_InLighthouseMode()
    {
        // Default validation mode is Lighthouse: FCU with payload attributes is forwarded
        // to the EL, the payloadId is tracked, and on engine_newPayload the proxy runs an
        // extra synthetic getPayload+newPayload validation cycle against the EL before
        // forwarding the original request. End-to-end: a Cancun block must be accepted.
        await StartNethermindAndProxyAsync(
            validationMode: "Lighthouse",
            getPayloadMethod: "engine_getPayloadV3",
            newPayloadMethod: "engine_newPayloadV3");

        using HttpClient proxyClient = await CreateAuthenticatedProxyClientAsync();
        await Utils.CreateBlocksAsync(proxyClient, count: 1, version: 3, minimumTimestamp: SepoliaCancunTimestamp);

        JsonNode head = await Utils.SendEngineRequestAsync(proxyClient, "eth_blockNumber");
        Assert.That(head.GetValue<string>(), Is.EqualTo("0x1"));
    }

    [Test]
    public async Task Proxy_ProducesBlock_InForkChoiceUpdatedMode_PassThrough()
    {
        // In ForkChoiceUpdated mode without --validate-all-blocks, FCU+payloadAttributes is
        // passed through unchanged; only NULL-payload-attribute FCUs would trigger the
        // synthetic validation cycle. This test asserts the pass-through path stays correct
        // for the normal CL flow.
        await StartNethermindAndProxyAsync(
            validationMode: "ForkChoiceUpdated",
            getPayloadMethod: "engine_getPayloadV3",
            newPayloadMethod: "engine_newPayloadV3");

        using HttpClient proxyClient = await CreateAuthenticatedProxyClientAsync();
        await Utils.CreateBlocksAsync(proxyClient, count: 1, version: 3, minimumTimestamp: SepoliaCancunTimestamp);

        JsonNode head = await Utils.SendEngineRequestAsync(proxyClient, "eth_blockNumber");
        Assert.That(head.GetValue<string>(), Is.EqualTo("0x1"));
    }

    [Test]
    public async Task Proxy_ReturnsNullPayloadId_InMergedMode()
    {
        // Merged mode contract: FCU returns payloadStatus=VALID with payloadId=null even
        // when the EL produced a payloadId. The proxy tracks the EL's payloadId internally
        // for later use, but hides it from the CL. The test asserts the response shape.
        await StartNethermindAndProxyAsync(
            validationMode: "Merged",
            getPayloadMethod: "engine_getPayloadV3",
            newPayloadMethod: "engine_newPayloadV3");

        using HttpClient proxyClient = await CreateAuthenticatedProxyClientAsync();

        JsonNode latestBlock = await Utils.SendEngineRequestAsync(proxyClient, "eth_getBlockByNumber", "latest", false);
        string parentHash = latestBlock["hash"].GetValue<string>();
        long parentTimestamp = Convert.ToInt64(latestBlock["timestamp"].GetValue<string>().Substring(2), 16);
        long newTimestamp = Math.Max(SepoliaCancunTimestamp, parentTimestamp + 12);

        object forkchoiceState = new
        {
            headBlockHash = parentHash,
            safeBlockHash = parentHash,
            finalizedBlockHash = parentHash
        };
        object payloadAttributes = new
        {
            timestamp = $"0x{newTimestamp:x}",
            prevRandao = "0x0000000000000000000000000000000000000000000000000000000000000000",
            suggestedFeeRecipient = "0x0000000000000000000000000000000000000000",
            withdrawals = Array.Empty<object>(),
            parentBeaconBlockRoot = "0x0000000000000000000000000000000000000000000000000000000000000000"
        };

        JsonNode fcuResult = await Utils.SendEngineRequestAsync(proxyClient, "engine_forkchoiceUpdatedV3", forkchoiceState, payloadAttributes);

        Assert.That(fcuResult["payloadStatus"]["status"].GetValue<string>(), Is.EqualTo("VALID"));
        Assert.That(fcuResult["payloadStatus"]["latestValidHash"].GetValue<string>(), Is.EqualTo(parentHash));
        Assert.That(fcuResult["payloadId"], Is.Null, "Merged mode hides payloadId from CL even when EL produced one");
    }

    [Test]
    public async Task Proxy_FailsToStart_WithoutEcEndpoint()
    {
        // Program.Main returns exit code 1 when --ec-endpoint is missing. We wait for the
        // matching error message to surface via the wait strategy instead of polling
        // _proxyContainer.State — Testcontainers caches that property and won't observe
        // the Running→Exited transition for a process that dies shortly after start.
        ContainerBuilder builder = (await Utils.BuildEngineApiProxyContainerAsync(new[] { "-p", "9551" }))
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Execution Client endpoint is required"));
        _proxyContainer = builder.Build();
        await _proxyContainer.StartAsync();

        string stdout = await _proxyContainer.GetCleanStdoutAsync();
        string stderr = await _proxyContainer.GetCleanStderrAsync();
        Assert.That(stdout + stderr, Does.Contain("Execution Client endpoint is required"));
    }

    [Test]
    public async Task Proxy_ReturnsErrorResponse_WhenExecutionClientUnreachable()
    {
        // No Nethermind container — proxy points at a bogus host so any forwarded request
        // fails network resolution. The proxy must still respond to the CL with a
        // structured JSON-RPC error (code -32603) rather than dropping the connection.
        string[] command =
        {
            "-e", "http://unreachable-el:8551",
            "-p", ProxyPort.ToString(),
            "--validation-mode", "Lighthouse",
            "--request-timeout", "5",
        };

        ContainerBuilder builder = (await Utils.BuildEngineApiProxyContainerAsync(command))
            .WithPortBinding(ProxyPort, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Engine API Proxy started on port"));
        _proxyContainer = builder.Build();
        await _proxyContainer.StartAsync();

        using HttpClient proxyClient = new()
        {
            BaseAddress = new Uri($"http://{_proxyContainer.Hostname}:{_proxyContainer.GetMappedPublicPort(ProxyPort)}")
        };

        JsonNode error = await SendRawJsonRpcAndReturnErrorAsync(proxyClient, "eth_blockNumber");
        Assert.That(error, Is.Not.Null, "proxy must return a structured JSON-RPC error when the EL is unreachable");
        Assert.That(error["code"].GetValue<int>(), Is.EqualTo(-32603));
    }

    private async Task StartNethermindAndProxyAsync(
        string validationMode = "Lighthouse",
        bool validateAllBlocks = false,
        string getPayloadMethod = "engine_getPayloadV4",
        string newPayloadMethod = "engine_newPayloadV4")
    {
        _network = new NetworkBuilder().Build();
        await _network.CreateAsync();

        string[] nethermindCommand =
        {
            "--config", "sepolia",
            "--JsonRpc.Enabled", "true",
            "--JsonRpc.Host", "0.0.0.0",
            "--JsonRpc.Port", "8545",
            "--JsonRpc.EnginePort", "8551",
            "--JsonRpc.EngineHost", "0.0.0.0",
            "--JsonRpc.JwtSecretFile", "jwt.hex",
            "--Merge.TerminalTotalDifficulty", "0",
            "--Merge.Enabled", "true",
            "--JsonRpc.EngineEnabledModules", "[Engine,Eth,Web3,Net]",
            "--Sync.NetworkingEnabled", "false",
            "--Sync.SynchronizationEnabled", "false",
            "--Sync.FastSync", "false",
            "--Sync.SnapSync", "false",
        };

        ContainerBuilder nethermindBuilder = (await Utils.BuildNethermindContainerAsync(nethermindCommand))
            .WithNetwork(_network)
            .WithNetworkAliases(NethermindNetworkAlias);
        _nethermindContainer = nethermindBuilder.Build();
        await _nethermindContainer.StartAsync();

        List<string> proxyArgs =
        [
            "-e", ProxyEcEndpoint,
            "-p", ProxyPort.ToString(),
            "--validation-mode", validationMode,
            "--get-payload-method", getPayloadMethod,
            "--new-payload-method", newPayloadMethod,
            "--log-level", "Debug",
        ];
        if (validateAllBlocks)
        {
            proxyArgs.Add("--validate-all-blocks");
        }

        ContainerBuilder proxyBuilder = (await Utils.BuildEngineApiProxyContainerAsync(proxyArgs.ToArray()))
            .WithNetwork(_network)
            .WithPortBinding(ProxyPort, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Engine API Proxy started on port"));
        _proxyContainer = proxyBuilder.Build();
        await _proxyContainer.StartAsync();
    }

    private async Task<HttpClient> CreateAuthenticatedProxyClientAsync()
    {
        ExecResult jwtRead = await _nethermindContainer.ExecAsync(new[] { "cat", "jwt.hex" });
        Assert.That(jwtRead.ExitCode, Is.EqualTo(0), $"could not read jwt.hex from Nethermind container: {jwtRead.Stderr}");

        Uri proxyUrl = new($"http://{_proxyContainer.Hostname}:{_proxyContainer.GetMappedPublicPort(ProxyPort)}");
        return Utils.CreateEngineHttpClient(proxyUrl, jwtRead.Stdout.Trim());
    }

    private static async Task<JsonNode> SendRawJsonRpcAndReturnErrorAsync(HttpClient client, string method, params object[] parameters)
    {
        var request = new
        {
            jsonrpc = "2.0",
            method,
            @params = parameters,
            id = 1
        };
        StringContent content = new(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
        HttpResponseMessage response = await client.PostAsync("", content);
        string body = await response.Content.ReadAsStringAsync();
        JsonNode json = JsonNode.Parse(body);
        return json?["error"];
    }
}
