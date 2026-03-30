using System;
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
        var defaultCommand = new[] { "--config", "none.json", "--JsonRpc.Enabled", "true", "--JsonRpc.Host", "0.0.0.0", "--JsonRpc.Port", "8545", "--JsonRpc.EnginePort", "8551", "--JsonRpc.EngineHost", "0.0.0.0", "--JsonRpc.JwtSecretFile", "jwt.hex" };
        var command = commandOverride ?? defaultCommand;

        var builder = new ContainerBuilder()
            .WithImage(image)
            .WithCommand(command)
            .WithPortBinding(8545, true);

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
        
        // Let it attempt to start and fail
        await Task.Delay(2000);
        
        var cleanStdout = await _nethermindContainer.GetCleanStdoutAsync();
        var cleanStderr = await _nethermindContainer.GetCleanStderrAsync();
        
        var combinedLogs = cleanStdout + cleanStderr;
        combinedLogs.Should().Contain("Configuration file not found");
    }
}
