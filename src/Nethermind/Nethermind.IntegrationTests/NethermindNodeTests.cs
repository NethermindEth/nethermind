using System;
using System.Net.Http;
using System.Net.Http.Headers;
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
        var image = Environment.GetEnvironmentVariable("NETHERMIND_IMAGE") ?? "nethermindeth/nethermind:latest";
        var defaultCommand = new[] { "--config", "sepolia", "--JsonRpc.Enabled", "true", "--JsonRpc.Host", "0.0.0.0", "--JsonRpc.Port", "8545", "--JsonRpc.EnginePort", "8551", "--JsonRpc.EngineHost", "0.0.0.0", "--JsonRpc.JwtSecretFile", "jwt.hex" };
        var command = commandOverride ?? defaultCommand;

        var builder = new ContainerBuilder()
            .WithImage(image)
            .WithCommand(command)
            .WithPortBinding(8545, true)
            .WithPortBinding(8551, true);

        if (waitForInit)
        {
            builder = builder.WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Initialization Completed"));
        }

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

        var rpcUrl = new Uri($"http://{_nethermindContainer.Hostname}:{_nethermindContainer.GetMappedPublicPort(8545)}");
        
        using var rpcClient = new BasicJsonRpcClient(rpcUrl, new EthereumJsonSerializer(), LimboLogs.Instance);
        var response = await rpcClient.Post("eth_blockNumber");
        
        response.Should().NotBeNullOrEmpty();
        response.Should().Contain("\"result\":");
    }

    [Test]
    public async Task Nethermind_ShouldLog_ExpectedMessages()
    {
        await StartContainerAsync();
        
        var cleanStdout = await _nethermindContainer.GetCleanStdoutAsync();
        
        cleanStdout.Should().Contain("Nethermind is starting up");
        cleanStdout.Should().Contain("Initialization Completed");
    }

    [Test]
    public async Task Nethermind_ShouldFail_WhenNoConfigProvided()
    {
        var commandWithoutConfig = new[] { "--config", "nonexistent.json" };
        
        await StartContainerAsync(commandWithoutConfig, waitForInit: false);
        
        await Task.Delay(2000);
        
        var cleanStdout = await _nethermindContainer.GetCleanStdoutAsync();
        var cleanStderr = await _nethermindContainer.GetCleanStderrAsync();
        
        var combinedLogs = cleanStdout + cleanStderr;
        combinedLogs.Should().Contain("Configuration file not found");
    }

    [Test]
    public async Task Nethermind_ShouldProduceBlock_ViaEngineApi()
    {
        var command = new[] 
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

        var execResult = await _nethermindContainer.ExecAsync(new[] { "cat", "jwt.hex" });
        execResult.ExitCode.Should().Be(0);
        var jwtSecretHex = execResult.Stdout.Trim();
        jwtSecretHex.Should().NotBeNullOrEmpty();

        var jwtToken = Utils.CreateJwtToken(jwtSecretHex);

        var engineUrl = new Uri($"http://{_nethermindContainer.Hostname}:{_nethermindContainer.GetMappedPublicPort(8551)}");
        
        using var httpClient = new HttpClient { BaseAddress = engineUrl };
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwtToken);
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // Produce a single V1 block starting from Sepolia Genesis Timestamp
        await Utils.CreateBlocksAsync(httpClient, 1, 1, 1633267481L);

        // Verify block is produced using standard RPC
        var currentBlockStr = await Utils.SendEngineRequestAsync(httpClient, "eth_blockNumber");
        var currentBlock = currentBlockStr.GetValue<string>();
        currentBlock.Should().Be("0x1");
        var result = await Utils.SendEngineRequestAsync(httpClient, "eth_syncing");
        result.GetValue<bool>().Should().Be(false);
    }

    [Test]
    public async Task Nethermind_ShouldProduceBlocks_DifferentVersions_ViaEngineApi()
    {
        var command = new[] 
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

        var execResult = await _nethermindContainer.ExecAsync(new[] { "cat", "jwt.hex" });
        var jwtSecretHex = execResult.Stdout.Trim();
        var jwtToken = Utils.CreateJwtToken(jwtSecretHex);

        var engineUrl = new Uri($"http://{_nethermindContainer.Hostname}:{_nethermindContainer.GetMappedPublicPort(8551)}");
        using var httpClient = new HttpClient { BaseAddress = engineUrl };
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwtToken);
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // V1 (Paris): starting from genesis timestamp 1633267481 (V1 range)
        await Utils.CreateBlocksAsync(httpClient, 100, 1, 1633267481L);
        
        // V2 (Shanghai): > 1677557088
        await Utils.CreateBlocksAsync(httpClient, 100, 2, 1677557088L + 1200);

        // V3 (Cancun): > 1706655072
        await Utils.CreateBlocksAsync(httpClient, 100, 3, 1706655072L + 1200);

        // Verify total blocks
        var currentBlockStr = await Utils.SendEngineRequestAsync(httpClient, "eth_blockNumber");
        var currentBlock = currentBlockStr.GetValue<string>();
        // 300 blocks produced, 0x12c
        currentBlock.Should().Be("0x12c");
    }
}
