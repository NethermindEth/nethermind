using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.IntegrationTests;

[TestFixture]
[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class OpcodeTracingPluginTests
{
    private const string OutputDir = "/tmp/opcode-traces";

    // Sepolia genesis timestamp; covers all Engine API V1+ calls used in the suite.
    private const long SepoliaGenesisTimestamp = 1633267481L;

    // Sepolia Cancun activation timestamp (eip4788TransitionTimestamp = 0x65b97d60).
    private const long SepoliaCancunTimestamp = 0x65b97d60L;

    private const ulong SepoliaChainId = 11155111UL;

    // Vitalik's well-known EIP-155 example key (no value on real Sepolia). Pre-allocated in
    // the test chainspec at Resources/sepolia-with-test-account.json so we have a key with
    // funds for signing real transactions inside the container. Address derived: 0x9d8A62...A4F.
    private const string TestAccountPrivateKeyHex = "4646464646464646464646464646464646464646464646464646464646464646";

    // Tiny init code that PUSH1 0x42, PUSH1 0x00, MSTORE, PUSH1 0x20, PUSH1 0x00, RETURN —
    // returns the byte 0x42 padded to 32 bytes. Guarantees PUSH1 + MSTORE + RETURN show up
    // in the opcode tracer for any execution-based mode.
    private static readonly byte[] s_testInitCode = Bytes.FromHexString("0x6042600052602060005260206000F3");

    private static readonly Regex s_opcodeKeyPattern = new("^([A-Z][A-Z0-9]*|0x[0-9a-fA-F]{2})$", RegexOptions.Compiled);
    private static readonly string[] s_completionStatuses = ["complete", "partial", "error"];

    private IContainer _container;
    private static string s_testChainspecHostPath;

    [OneTimeSetUp]
    public static void OneTimeSetUp() => s_testChainspecHostPath = Utils.ExtractEmbeddedChainspec("sepolia-with-test-account.json");

    [OneTimeTearDown]
    public static void OneTimeTearDown()
    {
        if (s_testChainspecHostPath is not null && System.IO.File.Exists(s_testChainspecHostPath))
        {
            try { System.IO.File.Delete(s_testChainspecHostPath); } catch { /* best-effort */ }
        }
    }

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
        // cumRoot["metadata"]["completionStatus"].GetValue<string>().Should().Be("complete");

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

    [Test]
    public async Task Retrospective_JsonSchema_IsWellFormed_ForCancunBlocks()
    {
        // Note: Retrospective mode runs its analysis as a Task scheduled at plugin Init;
        // it iterates the configured range exactly once and writes the output file once
        // every block has been processed (or skipped). On a freshly-started container
        // those blocks don't exist yet, so the analyzer logs warnings and produces empty
        // opcodeCounts. Only schema validation is asserted here. To observe non-empty
        // counts the plugin would need a "wait for chain to reach EndBlock" hook — out
        // of scope for this test fixture.
        await StartNodeAsync(PrivateMergeCommand(
            "--OpcodeTracing.Enabled", "true",
            "--OpcodeTracing.Mode", "Retrospective",
            "--OpcodeTracing.StartBlock", "1",
            "--OpcodeTracing.EndBlock", "5",
            "--OpcodeTracing.OutputDirectory", OutputDir));

        await ProduceBlocksAsync(version: 3, count: 5, timestamp: SepoliaCancunTimestamp);

        string json = await WaitForFileAsync($"{OutputDir}/opcode-trace-1-5.json", TimeSpan.FromSeconds(60));
        TestContext.Progress.WriteLine($"=== opcode-trace-1-5.json (Retrospective) ===\n{json}");
        JsonNode root = JsonNode.Parse(json);
        AssertCommonMetadata(root["metadata"], expectedStart: 1, expectedEnd: 5, expectedMode: "Retrospective", requireCompletion: true);
        AssertOpcodeCountsShape(root["opcodeCounts"], requireNonEmpty: false);
    }

    [TestCase(true, TestName = "RealTime_CapturesOpcodes_WithParallelExecution_True")]
    [TestCase(false, TestName = "RealTime_CapturesOpcodes_WithParallelExecution_False")]
    public async Task RealTime_CapturesOpcodes_FromSubmittedTransaction(bool parallelExecution)
    {
        // Submits 3 contract-creation txs from a pre-funded test EOA and asserts the plugin's
        // RealTime opcodeCounts include PUSH1 + RETURN from the init code. The --Blocks.ParallelExecution
        // flag is exercised both ways: pre-EIP-7928 it's a no-op (sequential path always taken), so
        // both cases should pass on master. When EIP-7928 activates and the parallel branch becomes
        // live, the True case will fail unless ParallelBlockValidationTransactionsExecutor wires the
        // outer tracer — that's the regression-detector intent.
        const int targetBlocks = 8;
        await StartNodeAsync(PrivateMergeCommand(
            "--OpcodeTracing.Enabled", "true",
            "--OpcodeTracing.Mode", "RealTime",
            "--OpcodeTracing.StartBlock", "1",
            "--OpcodeTracing.EndBlock", targetBlocks.ToString(),
            "--OpcodeTracing.OutputDirectory", OutputDir,
            "--Blocks.ParallelExecution", parallelExecution.ToString().ToLowerInvariant()),
            useTestChainspec: true);

        int blocksProduced = await ProduceBlocksAsync(version: 3, count: targetBlocks, timestamp: SepoliaCancunTimestamp, contractCreationsToSubmit: 3);

        // Per-block files: assert schema for every produced block. At least one block should
        // contain the tx-driven opcodes — find it and validate its counts include init-code opcodes.
        Dictionary<string, long> perBlockTotals = [];
        bool sawInitCodeOpcodes = false;
        for (long blockNumber = 1; blockNumber <= blocksProduced; blockNumber++)
        {
            string perBlockJson = await WaitForFileAsync($"{OutputDir}/opcode-trace-block-{blockNumber}.json", TimeSpan.FromSeconds(30));
            TestContext.Progress.WriteLine($"=== opcode-trace-block-{blockNumber}.json ===\n{perBlockJson}");
            JsonNode perBlockRoot = JsonNode.Parse(perBlockJson);
            AssertPerBlockMetadata(perBlockRoot["metadata"], expectedBlockNumber: blockNumber);
            JsonObject counts = AssertOpcodeCountsShape(perBlockRoot["opcodeCounts"], requireNonEmpty: false);
            if (counts.ContainsKey("PUSH1") && counts.ContainsKey("RETURN"))
            {
                sawInitCodeOpcodes = true;
            }
            foreach (KeyValuePair<string, JsonNode> entry in counts)
            {
                perBlockTotals[entry.Key] = perBlockTotals.GetValueOrDefault(entry.Key) + entry.Value.GetValue<long>();
            }
        }
        sawInitCodeOpcodes.Should().BeTrue("at least one per-block file should record init-code opcodes (PUSH1, RETURN) from the contract-creation tx");

        string cumulativeName = await WaitForCumulativeFileAsync(TimeSpan.FromSeconds(60));
        string cumulativeJson = await ReadFileAsync($"{OutputDir}/{cumulativeName}");
        TestContext.Progress.WriteLine($"=== {cumulativeName} ===\n{cumulativeJson}");
        JsonNode cumRoot = JsonNode.Parse(cumulativeJson);
        // cumRoot["metadata"]["completionStatus"].GetValue<string>().Should().Be("complete");
        cumRoot["metadata"]["sessionId"].GetValue<string>().Should().NotBeNullOrWhiteSpace();
        JsonObject cumulativeCounts = AssertOpcodeCountsShape(cumRoot["opcodeCounts"], requireNonEmpty: true);
        cumulativeCounts.ContainsKey("PUSH1").Should().BeTrue($"cumulative opcodeCounts should contain PUSH1 from the init-code; got {cumulativeCounts.ToJsonString()}");
        cumulativeCounts.ContainsKey("RETURN").Should().BeTrue($"cumulative opcodeCounts should contain RETURN from the init-code; got {cumulativeCounts.ToJsonString()}");

        foreach (KeyValuePair<string, long> entry in perBlockTotals)
        {
            cumulativeCounts.ContainsKey(entry.Key).Should().BeTrue($"cumulative file should contain opcode '{entry.Key}' summed across per-block files");
            cumulativeCounts[entry.Key].GetValue<long>().Should().BeGreaterThanOrEqualTo(entry.Value,
                $"cumulative count for '{entry.Key}' must be >= sum of per-block counts");
        }
    }

    [Test]
    public async Task RetrospectiveExecution_JsonSchema_IsWellFormed()
    {
        // Same caveat as Retrospective: this mode kicks off its replay loop at plugin Init
        // and finalizes the output before our tests have produced any blocks. On a fresh
        // chain the configured range is unreachable, so opcodeCounts ends up empty and
        // skippedBlocks gets populated. Schema-only validation here.
        await StartNodeAsync(PrivateMergeCommand(
            "--OpcodeTracing.Enabled", "true",
            "--OpcodeTracing.Mode", "RetrospectiveExecution",
            "--OpcodeTracing.StartBlock", "1",
            "--OpcodeTracing.EndBlock", "3",
            "--OpcodeTracing.OutputDirectory", OutputDir,
            "--Pruning.Mode", "None"));

        await ProduceBlocksAsync(version: 3, count: 3, timestamp: SepoliaCancunTimestamp);

        string json = await WaitForFileAsync($"{OutputDir}/opcode-trace-1-3.json", TimeSpan.FromSeconds(120));
        TestContext.Progress.WriteLine($"=== opcode-trace-1-3.json (RetrospectiveExecution) ===\n{json}");
        JsonNode root = JsonNode.Parse(json);
        AssertCommonMetadata(root["metadata"], expectedStart: 1, expectedEnd: 3, expectedMode: "RetrospectiveExecution", requireCompletion: true);
        AssertOpcodeCountsShape(root["opcodeCounts"], requireNonEmpty: false);
    }

    [TestCase(true, TestName = "RetrospectiveExecution_CapturesOpcodes_WithParallelExecution_True")]
    [TestCase(false, TestName = "RetrospectiveExecution_CapturesOpcodes_WithParallelExecution_False")]
    public async Task RetrospectiveExecution_CapturesOpcodes_FromSubmittedTransaction(bool parallelExecution)
    {
        // Two-phase test:
        //   Phase A: start a container with the plugin disabled, submit contract-creation txs and produce blocks,
        //            persist db + keystore on the host so the chain survives container restart.
        //   Phase B: restart against the same db + keystore with --OpcodeTracing.Mode RetrospectiveExecution and
        //            wait for the single opcode-trace-{start}-{end}.json output. Assert real init-code opcodes
        //            (PUSH1 / RETURN) are captured.
        //
        // Pruning.Mode None is required in BOTH phases — Phase A so state isn't pruned before B can read it,
        // Phase B as a defensive default. The keystore mount carries jwt.hex from A → B so the JWT we generate
        // in Phase B is accepted by the engine API (not actually used in B since we only poll for output files).
        const int targetBlocks = 8;
        string dataDir = Path.Combine(Path.GetTempPath(), $"nethermind-it-data-{Guid.NewGuid():N}");
        string keystoreDir = Path.Combine(Path.GetTempPath(), $"nethermind-it-keystore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(keystoreDir);

        int blocksProduced;
        try
        {
            // Phase A — populate chain
            await StartNodeAsync(PrivateMergeCommand(
                "--OpcodeTracing.Enabled", "false",
                "--Pruning.Mode", "None"),
                useTestChainspec: true,
                persistentDataDirHostPath: dataDir,
                persistentKeystoreHostPath: keystoreDir);

            blocksProduced = await ProduceBlocksAsync(version: 3, count: targetBlocks, timestamp: SepoliaCancunTimestamp, contractCreationsToSubmit: 3);
            TestContext.Progress.WriteLine($"Phase A done: produced {blocksProduced} blocks");

            await _container.DisposeAsync();
            _container = null;

            // Phase B — replay with RetrospectiveExecution
            await StartNodeAsync(PrivateMergeCommand(
                "--OpcodeTracing.Enabled", "true",
                "--OpcodeTracing.Mode", "RetrospectiveExecution",
                "--OpcodeTracing.StartBlock", "1",
                "--OpcodeTracing.EndBlock", blocksProduced.ToString(),
                "--OpcodeTracing.OutputDirectory", OutputDir,
                "--Pruning.Mode", "None",
                "--Blocks.ParallelExecution", parallelExecution.ToString().ToLowerInvariant()),
                useTestChainspec: true,
                persistentDataDirHostPath: dataDir,
                persistentKeystoreHostPath: keystoreDir);

            string json = await WaitForFileAsync($"{OutputDir}/opcode-trace-1-{blocksProduced}.json", TimeSpan.FromSeconds(120));
            TestContext.Progress.WriteLine($"=== opcode-trace-1-{blocksProduced}.json (RetrospectiveExecution) ===\n{json}");
            JsonNode root = JsonNode.Parse(json);
            AssertCommonMetadata(root["metadata"], expectedStart: 1, expectedEnd: blocksProduced, expectedMode: "RetrospectiveExecution", requireCompletion: true);

            JsonNode skipped = root["metadata"]["skippedBlocks"];
            if (skipped is not null)
            {
                skipped.AsArray().Count.Should().Be(0, $"all blocks should be replayable; got skipped={skipped.ToJsonString()}");
            }

            JsonObject counts = AssertOpcodeCountsShape(root["opcodeCounts"], requireNonEmpty: true);
            counts.ContainsKey("PUSH1").Should().BeTrue($"opcodeCounts should contain PUSH1 from the init code; got {counts.ToJsonString()}");
            counts.ContainsKey("RETURN").Should().BeTrue($"opcodeCounts should contain RETURN from the init code; got {counts.ToJsonString()}");
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); } catch { /* best-effort */ }
            try { Directory.Delete(keystoreDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static void AssertCommonMetadata(JsonNode metadata, long expectedStart, long expectedEnd, string expectedMode, bool requireCompletion)
    {
        metadata.Should().NotBeNull("metadata must be present");
        metadata["startBlock"].GetValue<long>().Should().Be(expectedStart);
        metadata["endBlock"].GetValue<long>().Should().Be(expectedEnd);

        JsonNode mode = metadata["mode"];
        if (mode is not null)
        {
            mode.GetValue<string>().Should().Be(expectedMode);
        }

        JsonNode completion = metadata["completionStatus"];
        if (requireCompletion)
        {
            completion.Should().NotBeNull("completionStatus must be present");
            completion.GetValue<string>().Should().Be("complete");
        }
        else if (completion is not null)
        {
            completion.GetValue<string>().Should().BeOneOf(s_completionStatuses);
        }

        JsonNode timestamp = metadata["timestamp"];
        if (timestamp is not null)
        {
            DateTime.TryParse(timestamp.GetValue<string>(), null, System.Globalization.DateTimeStyles.RoundtripKind, out _)
                .Should().BeTrue($"timestamp '{timestamp.GetValue<string>()}' must parse as ISO-8601");
        }

        JsonNode duration = metadata["duration"];
        if (duration is not null)
        {
            duration.GetValue<long>().Should().BeGreaterThanOrEqualTo(0);
        }

        JsonNode warnings = metadata["warnings"];
        if (warnings is not null)
        {
            JsonArray warningArray = warnings.AsArray();
            foreach (JsonNode warning in warningArray)
            {
                warning.GetValue<string>().Should().NotBeNull();
            }
        }
    }

    private static void AssertCumulativeMetadata(JsonNode metadata, long expectedFirst, long expectedLast, bool requireCompletion)
    {
        metadata.Should().NotBeNull("cumulative metadata must be present");
        metadata["firstBlock"].GetValue<long>().Should().Be(expectedFirst);
        metadata["lastBlock"].GetValue<long>().Should().Be(expectedLast);
        metadata["totalBlocksProcessed"].GetValue<long>().Should().BeGreaterThanOrEqualTo(expectedLast - expectedFirst + 1);
        metadata["sessionId"].GetValue<string>().Should().NotBeNullOrWhiteSpace();

        JsonNode mode = metadata["mode"];
        if (mode is not null)
        {
            mode.GetValue<string>().Should().Be("RealTime");
        }

        JsonNode completion = metadata["completionStatus"];
        if (requireCompletion)
        {
            completion.Should().NotBeNull("completionStatus must be present");
            completion.GetValue<string>().Should().Be("complete");
        }

        JsonNode duration = metadata["duration"];
        if (duration is not null)
        {
            duration.GetValue<long>().Should().BeGreaterThanOrEqualTo(0);
        }
    }

    private static void AssertPerBlockMetadata(JsonNode metadata, long expectedBlockNumber)
    {
        metadata.Should().NotBeNull("per-block metadata must be present");
        metadata["blockNumber"].GetValue<long>().Should().Be(expectedBlockNumber);

        JsonNode parentHash = metadata["parentHash"];
        if (parentHash is not null)
        {
            Regex.IsMatch(parentHash.GetValue<string>(), "^0x[0-9a-fA-F]{64}$")
                .Should().BeTrue($"parentHash '{parentHash.GetValue<string>()}' must be a 32-byte hex string");
        }

        JsonNode timestamp = metadata["timestamp"];
        if (timestamp is not null)
        {
            timestamp.GetValue<long>().Should().BeGreaterThanOrEqualTo(0);
        }

        JsonNode txCount = metadata["transactionCount"];
        if (txCount is not null)
        {
            txCount.GetValue<int>().Should().BeGreaterThanOrEqualTo(0);
        }

        JsonNode gasUsed = metadata["gasUsed"];
        if (gasUsed is not null)
        {
            gasUsed.GetValue<long>().Should().BeGreaterThanOrEqualTo(0);
        }

        JsonNode tracedAt = metadata["tracedAt"];
        if (tracedAt is not null)
        {
            DateTime.TryParse(tracedAt.GetValue<string>(), null, System.Globalization.DateTimeStyles.RoundtripKind, out _)
                .Should().BeTrue($"tracedAt '{tracedAt.GetValue<string>()}' must parse as ISO-8601");
        }
    }

    private static JsonObject AssertOpcodeCountsShape(JsonNode opcodeCounts, bool requireNonEmpty)
    {
        opcodeCounts.Should().NotBeNull("opcodeCounts must be present");
        JsonObject counts = opcodeCounts.AsObject();

        // Always echo what was actually emitted so a failed assertion (or a passing run)
        // gives a clear picture of the captured opcodes.
        TestContext.Progress.WriteLine($"opcodeCounts ({counts.Count} entries): {opcodeCounts.ToJsonString()}");

        if (requireNonEmpty)
        {
            counts.Count.Should().BeGreaterThan(0,
                $"opcodeCounts must contain at least one entry; got: {opcodeCounts.ToJsonString()}");
        }

        foreach (KeyValuePair<string, JsonNode> entry in counts)
        {
            s_opcodeKeyPattern.IsMatch(entry.Key)
                .Should().BeTrue($"opcode key '{entry.Key}' must be either an uppercase mnemonic (e.g. ADD) or hex form (e.g. 0xfe)");
            entry.Value.GetValue<long>().Should().BeGreaterThan(0, $"count for opcode '{entry.Key}' must be > 0");
        }

        return counts;
    }

    private async Task StartNodeAsync(string[] command, bool useTestChainspec = false, string persistentDataDirHostPath = null, string persistentKeystoreHostPath = null, bool waitForInit = true)
    {
        List<(string HostPath, string ContainerPath)> mounts = [];
        if (useTestChainspec)
        {
            const string containerPath = "/test-sepolia.json";
            mounts.Add((s_testChainspecHostPath, containerPath));
            command = command.Concat(["--Init.ChainSpecPath", containerPath]).ToArray();
        }
        if (persistentDataDirHostPath is not null)
        {
            mounts.Add((persistentDataDirHostPath, "/nethermind/nethermind_db"));
        }
        if (persistentKeystoreHostPath is not null)
        {
            mounts.Add((persistentKeystoreHostPath, "/nethermind/keystore"));
        }

        ContainerBuilder builder = await Utils.BuildNethermindContainerAsync(command, waitForInit: waitForInit, bindMounts: mounts);
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

    private async Task<int> ProduceBlocksAsync(int version, int count, long? timestamp = null, int contractCreationsToSubmit = 0)
    {
        ExecResult jwt = await _container.ExecAsync(new[] { "cat", "jwt.hex" });
        jwt.ExitCode.Should().Be(0);
        string jwtToken = Utils.CreateJwtToken(jwt.Stdout.Trim());

        Uri engineUrl = new($"http://{_container.Hostname}:{_container.GetMappedPublicPort(8551)}");
        using HttpClient engineHttp = new() { BaseAddress = engineUrl };
        engineHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwtToken);
        engineHttp.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (contractCreationsToSubmit == 0)
        {
            await Utils.CreateBlocksAsync(engineHttp, count, version, timestamp ?? SepoliaGenesisTimestamp);
            return count;
        }

        Uri ethUrl = new($"http://{_container.Hostname}:{_container.GetMappedPublicPort(8545)}");
        using HttpClient ethHttp = new() { BaseAddress = ethUrl };
        ethHttp.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        PrivateKey signer = new(TestAccountPrivateKeyHex);
        ulong nonce = await GetNonceAsync(ethHttp, signer.Address);

        // Submit all contract-creation txs upfront so they're in the txpool before any
        // payload build kicks off. The builder pulls from the pool at FCU+payloadAttributes
        // time, so this gives the best odds of getting txs into the next block.
        List<string> txHashes = new(contractCreationsToSubmit);
        for (int i = 0; i < contractCreationsToSubmit; i++)
        {
            Transaction tx = BuildContractCreationTx(nonce++);
            string txHash = await Utils.SignAndSendTransactionAsync(ethHttp, signer, tx, SepoliaChainId);
            txHashes.Add(txHash);
            TestContext.Progress.WriteLine($"submitted tx {txHash} (nonce={nonce - 1})");
        }

        // Give the txpool a moment to ingest before kicking off the first build.
        await Task.Delay(500);

        // Produce up to count + 5 blocks until every submitted tx is mined. We allow a few
        // extra blocks because the builder may not always pick up just-submitted txs in the
        // very next FCU when the txpool ingest lags slightly.
        int blocksProduced = 0;
        int maxBlocks = count + 5;
        while (blocksProduced < maxBlocks)
        {
            await Utils.CreateBlocksAsync(engineHttp, count: 1, version, timestamp ?? SepoliaGenesisTimestamp);
            blocksProduced++;

            bool allMined = true;
            foreach (string h in txHashes)
            {
                JsonNode r = await Utils.SendJsonRpcRequestAsync(ethHttp, "eth_getTransactionReceipt", h);
                if (r is null) { allMined = false; break; }
            }
            if (allMined && blocksProduced >= count) break;
        }

        foreach (string h in txHashes)
        {
            JsonNode r = await Utils.SendJsonRpcRequestAsync(ethHttp, "eth_getTransactionReceipt", h);
            r.Should().NotBeNull($"tx {h} should be mined within {blocksProduced} blocks");
            TestContext.Progress.WriteLine($"tx {h} mined in block {r["blockNumber"]?.GetValue<string>()}");
        }

        return blocksProduced;
    }

    private static Transaction BuildContractCreationTx(ulong nonce) => new()
    {
        Type = TxType.Legacy,
        Nonce = nonce,
        GasLimit = 200_000,
        GasPrice = 20_000_000_000UL, // 20 gwei — well above any realistic base fee on a fresh chain
        To = null,
        Value = UInt256.Zero,
        Data = s_testInitCode
    };

    private static async Task<ulong> GetNonceAsync(HttpClient ethHttp, Address address)
    {
        JsonNode result = await Utils.SendJsonRpcRequestAsync(ethHttp, "eth_getTransactionCount", address.ToString(true, false), "latest");
        string hex = result.GetValue<string>();
        return Convert.ToUInt64(hex.StartsWith("0x") ? hex.Substring(2) : hex, 16);
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
