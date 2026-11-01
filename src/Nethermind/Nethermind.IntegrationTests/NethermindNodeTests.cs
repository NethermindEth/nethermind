using System;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Nethermind.JsonRpc.Client;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.IntegrationTests;

[TestFixture]
[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class NethermindNodeTests
{
    private IContainer _nethermindContainer;

    private async Task StartContainerAsync(string[] commandOverride = null, bool waitForInit = true, bool suppressStartFailures = false,
        (string Path, string Contents)? configFile = null)
    {
        string[] defaultCommand = new[] { "--config", "sepolia", "--JsonRpc.Enabled", "true", "--JsonRpc.Host", "0.0.0.0", "--JsonRpc.Port", "8545", "--JsonRpc.EnginePort", "8551", "--JsonRpc.EngineHost", "0.0.0.0", "--JsonRpc.JwtSecretFile", "jwt.hex" };
        string[] command = commandOverride ?? defaultCommand;

        ContainerBuilder builder = await Utils.BuildNethermindContainerAsync(command, waitForInit);
        if (configFile is { } file)
            builder = builder.WithResourceMapping(Encoding.UTF8.GetBytes(file.Contents), file.Path);

        _nethermindContainer = builder.Build();

        try
        {
            await _nethermindContainer.StartAsync();
        }
        catch when (suppressStartFailures)
        {
        }
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_nethermindContainer != null)
        {
            await _nethermindContainer.WriteDiagnosticLogsAsync("Nethermind");
            await _nethermindContainer.DisposeAsync();
        }
    }

    [Test]
    public async Task Nethermind_ShouldRespondTo_EthBlockNumber()
    {
        await StartContainerAsync();
        Assert.That(_nethermindContainer.State, Is.EqualTo(TestcontainersStates.Running));

        Uri rpcUrl = new($"http://{_nethermindContainer.Hostname}:{_nethermindContainer.GetMappedPublicPort(8545)}");

        using BasicJsonRpcClient rpcClient = new(rpcUrl, new EthereumJsonSerializer(), LimboLogs.Instance);
        string response = await rpcClient.Post("eth_blockNumber");

        Assert.That(response, Is.Not.Null.And.Not.Empty);
        Assert.That(response, Does.Contain("\"result\":"));
    }

    [Test]
    public async Task Nethermind_ShouldLog_ExpectedMessages()
    {
        await StartContainerAsync();

        string cleanStdout = await _nethermindContainer.GetCleanStdoutAsync();

        Assert.That(cleanStdout, Does.Contain("Nethermind is starting up"));
        Assert.That(cleanStdout, Does.Contain("Initialization Completed"));
    }

    [Test]
    public async Task Nethermind_ShouldFail_WhenNoConfigProvided([Values] bool maskSensitiveData)
    {
        const string privatePath = "private-plugin-path-74031";
        string[] commandWithoutConfig = maskSensitiveData
            ? ["--config", "nonexistent.json", "--Init.MaskSensitiveData", "true", "--plugins-dir", privatePath]
            : ["--config", "nonexistent.json"];

        await StartContainerAsync(commandWithoutConfig, waitForInit: false, suppressStartFailures: true);

        string combinedLogs = await GetExitedContainerLogsAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(combinedLogs, Does.Contain("Configuration file not found.")
                .And.Contain("Built-in configurations:")
                .And.Not.Contain("nonexistent.json"));
            if (maskSensitiveData)
            {
                Assert.That(combinedLogs, Does.Contain("Plugin assemblies folder [redacted] was not found."));
                Assert.That(combinedLogs, Does.Not.Contain(privatePath));
            }
        }
    }

    [Test]
    public async Task Nethermind_ShouldReportJsonPosition_WhenConfigIsMalformed([Values] bool maskSensitiveData)
    {
        const string path = "/private-config-test.json";
        const string contents = "{\n  \"Init\": \"private-config-value\",\n  !\n}";
        await StartContainerAsync(["--config", path, "--Init.MaskSensitiveData", maskSensitiveData.ToString()],
            waitForInit: false, suppressStartFailures: true, configFile: (path, contents));

        string combinedLogs = await GetExitedContainerLogsAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(combinedLogs, Does.Contain("Invalid JSON at line 2, byte position 2."));
            Assert.That(combinedLogs, Does.Not.Contain(path).And.Not.Contain("private-config-value"));
        }
    }

    private async Task<string> GetExitedContainerLogsAsync()
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline && _nethermindContainer.State != TestcontainersStates.Exited)
            await Task.Delay(200);

        Assert.That(_nethermindContainer.State, Is.EqualTo(TestcontainersStates.Exited));
        return await _nethermindContainer.GetCleanStdoutAsync() + await _nethermindContainer.GetCleanStderrAsync();
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
        Assert.That(_nethermindContainer.State, Is.EqualTo(TestcontainersStates.Running));

        ExecResult execResult = await _nethermindContainer.ExecAsync(new[] { "cat", "jwt.hex" });
        Assert.That(execResult.ExitCode, Is.EqualTo(0));
        string jwtSecretHex = execResult.Stdout.Trim();
        Assert.That(jwtSecretHex, Is.Not.Null.And.Not.Empty);

        Uri engineUrl = new($"http://{_nethermindContainer.Hostname}:{_nethermindContainer.GetMappedPublicPort(8551)}");
        using HttpClient httpClient = Utils.CreateEngineHttpClient(engineUrl, jwtSecretHex);

        // Produce a single V1 block starting from Sepolia genesis timestamp.
        await Utils.CreateBlocksAsync(httpClient, 1, 1, 1633267481L);

        // Verify block is produced using standard RPC
        JsonNode currentBlockStr = await Utils.SendEngineRequestAsync(httpClient, "eth_blockNumber");
        string currentBlock = currentBlockStr.GetValue<string>();
        Assert.That(currentBlock, Is.EqualTo("0x1"));
        JsonNode result = await Utils.SendEngineRequestAsync(httpClient, "eth_syncing");
        Assert.That(result.GetValue<bool>(), Is.False);
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
        Assert.That(_nethermindContainer.State, Is.EqualTo(TestcontainersStates.Running));

        ExecResult execResult = await _nethermindContainer.ExecAsync(new[] { "cat", "jwt.hex" });
        string jwtSecretHex = execResult.Stdout.Trim();

        Uri engineUrl = new($"http://{_nethermindContainer.Hostname}:{_nethermindContainer.GetMappedPublicPort(8551)}");
        using HttpClient httpClient = Utils.CreateEngineHttpClient(engineUrl, jwtSecretHex);

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
        Assert.That(currentBlock, Is.EqualTo("0x12c"));
    }
}
