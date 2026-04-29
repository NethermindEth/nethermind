using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using Nethermind.JsonRpc.Client;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.IntegrationTests;

[TestFixture]
public class NethermindNodeTests
{
    private IContainer _nethermindContainer;

    private async Task StartContainerAsync(string[] commandOverride = null, bool waitForInit = true)
    {
        string[] defaultCommand = new[] { "--config", "sepolia", "--JsonRpc.Enabled", "true", "--JsonRpc.Host", "0.0.0.0", "--JsonRpc.Port", "8545", "--JsonRpc.EnginePort", "8551", "--JsonRpc.EngineHost", "0.0.0.0", "--JsonRpc.JwtSecretFile", "jwt.hex" };
        string[] command = commandOverride ?? defaultCommand;

        ContainerBuilder builder = await Utils.BuildNethermindContainerAsync(command, waitForInit);

        _nethermindContainer = builder.Build();

        try
        {
            await _nethermindContainer.StartAsync();
        }
        catch
        {
            // Ignore start failures in negative tests; assertions will be made on container state/logs
        }
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_nethermindContainer != null)
        {
            await _nethermindContainer.DisposeAsync();
        }
    }

    [Test]
    public async Task Nethermind_ShouldRespondTo_EthBlockNumber()
    {
        await StartContainerAsync();
        _nethermindContainer.State.Should().Be(TestcontainersStates.Running);

        Uri rpcUrl = new($"http://{_nethermindContainer.Hostname}:{_nethermindContainer.GetMappedPublicPort(8545)}");

        using BasicJsonRpcClient rpcClient = new(rpcUrl, new EthereumJsonSerializer(), LimboLogs.Instance);
        string response = await rpcClient.Post("eth_blockNumber");

        response.Should().NotBeNullOrEmpty();
        response.Should().Contain("\"result\":");
    }

    [Test]
    public async Task Nethermind_ShouldLog_ExpectedMessages()
    {
        await StartContainerAsync();

        string cleanStdout = await _nethermindContainer.GetCleanStdoutAsync();

        cleanStdout.Should().Contain("Nethermind is starting up");
        cleanStdout.Should().Contain("Initialization Completed");
    }

    [Test]
    public async Task Nethermind_ShouldFail_WhenNoConfigProvided()
    {
        string[] commandWithoutConfig = new[] { "--config", "nonexistent.json" };

        await StartContainerAsync(commandWithoutConfig, waitForInit: false);

        await Task.Delay(2000);

        string cleanStdout = await _nethermindContainer.GetCleanStdoutAsync();
        string cleanStderr = await _nethermindContainer.GetCleanStderrAsync();

        string combinedLogs = cleanStdout + cleanStderr;
        combinedLogs.Should().Contain("Configuration file not found");
    }

    [Test]
    public async Task Nethermind_ShouldProduceBlock_ViaEngineApi()
    {
        string[] command = new[]
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
            "--JsonRpc.EngineEnabledModules", "[Engine,Eth]",
            "--Sync.NetworkingEnabled", "false",
            "--Sync.SynchronizationEnabled", "false",
            "--Sync.FastSync", "false",
            "--Sync.SnapSync", "false"
        };
        await StartContainerAsync(command);
        _nethermindContainer.State.Should().Be(TestcontainersStates.Running);

        ExecResult execResult = await _nethermindContainer.ExecAsync(new[] { "cat", "jwt.hex" });
        execResult.ExitCode.Should().Be(0);
        string jwtSecretHex = execResult.Stdout.Trim();
        jwtSecretHex.Should().NotBeNullOrEmpty();

        string jwtToken = Utils.CreateJwtToken(jwtSecretHex);

        Uri engineUrl = new($"http://{_nethermindContainer.Hostname}:{_nethermindContainer.GetMappedPublicPort(8551)}");

        using HttpClient httpClient = new() { BaseAddress = engineUrl };
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwtToken);
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // Produce a single V1 block starting from Sepolia Genesis Timestamp
        await Utils.CreateBlocksAsync(httpClient, 1, 1, 1633267481L);

        // Verify block is produced using standard RPC
        JsonNode currentBlockStr = await Utils.SendEngineRequestAsync(httpClient, "eth_blockNumber");
        string currentBlock = currentBlockStr.GetValue<string>();
        currentBlock.Should().Be("0x1");
        JsonNode result = await Utils.SendEngineRequestAsync(httpClient, "eth_syncing");
        result.GetValue<bool>().Should().Be(false);
    }

    [Test]
    public async Task Nethermind_ShouldProduceBlocks_DifferentVersions_ViaEngineApi()
    {
        string[] command = new[]
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
            "--JsonRpc.EngineEnabledModules", "[Engine,Eth]",
            "--Sync.NetworkingEnabled", "false",
            "--Sync.SynchronizationEnabled", "false",
            "--Sync.FastSync", "false",
            "--Sync.SnapSync", "false"
        };
        await StartContainerAsync(command);
        _nethermindContainer.State.Should().Be(TestcontainersStates.Running);

        ExecResult execResult = await _nethermindContainer.ExecAsync(new[] { "cat", "jwt.hex" });
        string jwtSecretHex = execResult.Stdout.Trim();
        string jwtToken = Utils.CreateJwtToken(jwtSecretHex);

        Uri engineUrl = new($"http://{_nethermindContainer.Hostname}:{_nethermindContainer.GetMappedPublicPort(8551)}");
        using HttpClient httpClient = new() { BaseAddress = engineUrl };
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwtToken);
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // V1 (Paris): starting from genesis timestamp 1633267481 (V1 range)
        await Utils.CreateBlocksAsync(httpClient, 100, 1, 1633267481L);

        // V2 (Shanghai): > 1677557088
        await Utils.CreateBlocksAsync(httpClient, 100, 2, 1677557088L + 1200);

        // V3 (Cancun): > 1706655072
        await Utils.CreateBlocksAsync(httpClient, 100, 3, 1706655072L + 1200);

        // Verify total blocks
        JsonNode currentBlockStr = await Utils.SendEngineRequestAsync(httpClient, "eth_blockNumber");
        string currentBlock = currentBlockStr.GetValue<string>();
        // 300 blocks produced, 0x12c
        currentBlock.Should().Be("0x12c");
    }
}
