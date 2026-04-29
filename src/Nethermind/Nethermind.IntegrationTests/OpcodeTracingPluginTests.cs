using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using NUnit.Framework;

namespace Nethermind.IntegrationTests;

[TestFixture]
public class OpcodeTracingPluginTests
{
    private const string OutputDir = "/tmp/opcode-traces";

    // Sepolia genesis timestamp; covers all Engine API V1+ calls used in the suite.
    private const long SepoliaGenesisTimestamp = 1633267481L;

    private IContainer _container;

    [TearDown]
    public async Task TearDown()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
            _container = null;
        }
    }

    [Test]
    public async Task Plugin_LoadsAndLogsInitialization_WhenEnabled()
    {
        await StartNodeAsync(PrivateMergeCommand(
            "--OpcodeTracing.Enabled", "true",
            "--OpcodeTracing.Mode", "RealTime",
            "--OpcodeTracing.StartBlock", "1",
            "--OpcodeTracing.EndBlock", "1",
            "--OpcodeTracing.OutputDirectory", OutputDir));

        _container.State.Should().Be(TestcontainersStates.Running);

        string stdout = await _container.GetCleanStdoutAsync();
        stdout.Should().Contain("Initialization Completed");
        stdout.Should().Contain("Opcode tracing plugin initialized");
    }

    [Test]
    public async Task Plugin_DoesNothing_WhenEnabledFalse()
    {
        await StartNodeAsync(PrivateMergeCommand(
            "--OpcodeTracing.Enabled", "false",
            "--OpcodeTracing.OutputDirectory", OutputDir));

        await ProduceBlocksAsync(version: 1, count: 1);

        string stdout = await _container.GetCleanStdoutAsync();
        stdout.Should().NotContain("Opcode tracing plugin initialized");

        ExecResult ls = await _container.ExecAsync(new[] { "ls", "-1", OutputDir });
        // Either the directory was never created (non-zero exit) or it is empty.
        if (ls.ExitCode == 0)
        {
            ls.Stdout.Trim().Should().BeEmpty($"output dir should be empty when plugin is disabled, got: {ls.Stdout}");
        }
    }

    [Test]
    public async Task Retrospective_WritesSingleJsonFile_WithDocumentedSchema()
    {
        await StartNodeAsync(PrivateMergeCommand(
            "--OpcodeTracing.Enabled", "true",
            "--OpcodeTracing.Mode", "Retrospective",
            "--OpcodeTracing.StartBlock", "1",
            "--OpcodeTracing.EndBlock", "5",
            "--OpcodeTracing.OutputDirectory", OutputDir));

        await ProduceBlocksAsync(version: 1, count: 5);

        // Retrospective tracer waits for blocks 1-5 to exist, then emits a single trace file.
        string json = await WaitForFileAsync($"{OutputDir}/opcode-trace-1-5.json", TimeSpan.FromSeconds(60));
        JsonNode root = JsonNode.Parse(json);
        JsonNode metadata = root["metadata"];

        metadata["startBlock"].GetValue<long>().Should().Be(1);
        metadata["endBlock"].GetValue<long>().Should().Be(5);
        metadata["mode"].GetValue<string>().Should().Be("Retrospective");
        metadata["completionStatus"].GetValue<string>().Should().Be("complete");
        root["opcodeCounts"].Should().NotBeNull("opcodeCounts must be present even if empty");
    }

    [Test]
    public async Task RealTime_WritesPerBlockAndCumulativeFiles()
    {
        await StartNodeAsync(PrivateMergeCommand(
            "--OpcodeTracing.Enabled", "true",
            "--OpcodeTracing.Mode", "RealTime",
            "--OpcodeTracing.StartBlock", "1",
            "--OpcodeTracing.EndBlock", "3",
            "--OpcodeTracing.OutputDirectory", OutputDir));

        await ProduceBlocksAsync(version: 1, count: 3);

        // Expect three per-block files plus exactly one cumulative session file once block 3 is reached.
        await WaitForFileAsync($"{OutputDir}/opcode-trace-block-1.json", TimeSpan.FromSeconds(30));
        await WaitForFileAsync($"{OutputDir}/opcode-trace-block-2.json", TimeSpan.FromSeconds(30));
        await WaitForFileAsync($"{OutputDir}/opcode-trace-block-3.json", TimeSpan.FromSeconds(30));

        string cumulativeName = await WaitForCumulativeFileAsync(TimeSpan.FromSeconds(60));
        string cumulativeJson = await ReadFileAsync($"{OutputDir}/{cumulativeName}");
        JsonNode cumRoot = JsonNode.Parse(cumulativeJson);
        cumRoot["metadata"]["completionStatus"].GetValue<string>().Should().Be("complete");

        string perBlock1 = await ReadFileAsync($"{OutputDir}/opcode-trace-block-1.json");
        JsonNode block1Root = JsonNode.Parse(perBlock1);
        block1Root["metadata"]["blockNumber"].GetValue<long>().Should().Be(1);
    }

    [Test]
    public async Task InvalidRange_LogsError_AndDoesNotProduceFiles()
    {
        await StartNodeAsync(PrivateMergeCommand(
            "--OpcodeTracing.Enabled", "true",
            "--OpcodeTracing.Mode", "Retrospective",
            "--OpcodeTracing.StartBlock", "10",
            "--OpcodeTracing.EndBlock", "5",
            "--OpcodeTracing.OutputDirectory", OutputDir));

        // Plugin should detect the invalid range during Init (within ~5s per README).
        await Task.Delay(TimeSpan.FromSeconds(5));

        string stdout = await _container.GetCleanStdoutAsync();
        stdout.Should().Contain("Invalid range: StartBlock (10) > EndBlock (5)");

        ExecResult ls = await _container.ExecAsync(new[] { "ls", "-1", OutputDir });
        if (ls.ExitCode == 0)
        {
            ls.Stdout.Trim().Should().BeEmpty($"no trace files should be produced for invalid config, got: {ls.Stdout}");
        }
    }

    private async Task StartNodeAsync(string[] command)
    {
        ContainerBuilder builder = await Utils.BuildNethermindContainerAsync(command, waitForInit: true);
        _container = builder.Build();
        await _container.StartAsync();
    }

    private static string[] PrivateMergeCommand(params string[] extra)
    {
        string[] baseArgs = new[]
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
            "--Sync.SnapSync", "false",
        };
        return baseArgs.Concat(extra).ToArray();
    }

    private async Task ProduceBlocksAsync(int version, int count)
    {
        ExecResult jwt = await _container.ExecAsync(new[] { "cat", "jwt.hex" });
        jwt.ExitCode.Should().Be(0);
        string jwtToken = Utils.CreateJwtToken(jwt.Stdout.Trim());

        Uri engineUrl = new($"http://{_container.Hostname}:{_container.GetMappedPublicPort(8551)}");
        using HttpClient http = new() { BaseAddress = engineUrl };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwtToken);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        await Utils.CreateBlocksAsync(http, count, version, SepoliaGenesisTimestamp);
    }

    private async Task<string> ReadFileAsync(string path)
    {
        ExecResult result = await _container.ExecAsync(new[] { "cat", path });
        result.ExitCode.Should().Be(0, $"cat {path} failed: {result.Stderr}");
        return result.Stdout;
    }

    private async Task<string> WaitForFileAsync(string path, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ExecResult result = await _container.ExecAsync(new[] { "cat", path });
            if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Stdout))
            {
                return result.Stdout;
            }
            await Task.Delay(500);
        }
        throw new TimeoutException($"File {path} did not appear within {timeout}.");
    }

    private async Task<string> WaitForCumulativeFileAsync(TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ExecResult ls = await _container.ExecAsync(new[] { "sh", "-c", $"ls -1 {OutputDir} 2>/dev/null | grep -E '^opcode-trace-all-' || true" });
            string match = ls.Stdout?.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.IsNullOrEmpty(match))
            {
                return match;
            }
            await Task.Delay(500);
        }
        throw new TimeoutException($"Cumulative opcode-trace-all-*.json did not appear within {timeout}.");
    }
}
