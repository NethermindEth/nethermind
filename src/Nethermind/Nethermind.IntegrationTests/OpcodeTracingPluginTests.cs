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

    private const long SepoliaGenesisTimestamp = 1633267481L;
    private const long SepoliaCancunTimestamp = 0x65b97d60L;

    // EIP-7928 / EIP-7843 activation in the Amsterdam test chainspec.
    private const long AmsterdamTimestamp = 0x68edfd60L;

    private const ulong SepoliaChainId = 11155111UL;

    // Pre-allocated in Resources/sepolia-with-test-account.json (Vitalik's EIP-155 example key).
    private const string TestAccountPrivateKeyHex = "4646464646464646464646464646464646464646464646464646464646464646";

    // PUSH1 0x42 / MSTORE / RETURN — guarantees PUSH1 + RETURN in the tracer.
    private static readonly byte[] s_testInitCode = Bytes.FromHexString("0x6042600052602060005260206000F3");

    private static readonly Regex s_opcodeKeyPattern = new("^([A-Z][A-Z0-9]*|0x[0-9a-fA-F]{2})$", RegexOptions.Compiled);
    private static readonly string[] s_completionStatuses = ["complete", "partial", "error"];

    private IContainer _container;
    private static string s_testChainspecHostPath;
    private static string s_amsterdamChainspecHostPath;

    [OneTimeSetUp]
    public static void OneTimeSetUp()
    {
        s_testChainspecHostPath = Utils.ExtractEmbeddedChainspec("sepolia-with-test-account.json");
        s_amsterdamChainspecHostPath = Utils.ExtractEmbeddedChainspec("sepolia-amsterdam-with-test-account.json");
    }

    [OneTimeTearDown]
    public static void OneTimeTearDown()
    {
        foreach (string path in new[] { s_testChainspecHostPath, s_amsterdamChainspecHostPath })
        {
            if (path is not null && System.IO.File.Exists(path))
            {
                try { System.IO.File.Delete(path); }
                catch (Exception ex) { TestContext.Progress.WriteLine($"cleanup of {path} failed: {ex.Message}"); }
            }
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

        Assert.That(_container.State, Is.EqualTo(TestcontainersStates.Running));

        string stdout = await _container.GetCleanStdoutAsync();
        Assert.That(stdout, Does.Contain("Initialization Completed"));
        Assert.That(stdout, Does.Contain("Opcode tracing attached to block processor (RealTime mode"));
    }

    [Test]
    public async Task Plugin_DoesNothing_WhenEnabledFalse()
    {
        await StartNodeAsync(PrivateMergeCommand(
            "--OpcodeTracing.Enabled", "false",
            "--OpcodeTracing.OutputDirectory", OutputDir));

        await ProduceBlocksAsync(version: 1, count: 1);

        string stdout = await _container.GetCleanStdoutAsync();
        Assert.That(stdout, Does.Not.Contain("Opcode tracing plugin initialized"));

        ExecResult ls = await _container.ExecAsync(new[] { "ls", "-1", OutputDir });
        // Either the directory was never created (non-zero exit) or it is empty.
        if (ls.ExitCode == 0)
        {
            Assert.That(ls.Stdout.Trim(), Is.Empty, $"output dir should be empty when plugin is disabled, got: {ls.Stdout}");
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

        Assert.That(metadata["startBlock"].GetValue<long>(), Is.EqualTo(1));
        Assert.That(metadata["endBlock"].GetValue<long>(), Is.EqualTo(5));
        Assert.That(metadata["mode"].GetValue<string>(), Is.EqualTo("Retrospective"));
        Assert.That(metadata["completionStatus"].GetValue<string>(), Is.EqualTo("complete"));
        Assert.That(root["opcodeCounts"], Is.Not.Null, "opcodeCounts must be present even if empty");
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

        string perBlock1 = await ReadFileAsync($"{OutputDir}/opcode-trace-block-1.json");
        JsonNode block1Root = JsonNode.Parse(perBlock1);
        Assert.That(block1Root["metadata"]["blockNumber"].GetValue<long>(), Is.EqualTo(1));
    }

    [Test]
    [Ignore("Opcode tracing does not currently validate an inverted retrospective range during startup; enable when the plugin emits the asserted diagnostic.")]
    public async Task InvalidRange_LogsError_AndDoesNotProduceFiles()
    {
        await StartNodeAsync(PrivateMergeCommand(
            "--OpcodeTracing.Enabled", "true",
            "--OpcodeTracing.Mode", "Retrospective",
            "--OpcodeTracing.StartBlock", "10",
            "--OpcodeTracing.EndBlock", "5",
            "--OpcodeTracing.OutputDirectory", OutputDir),
            waitForInit: false);

        // Do not wait for the success-only initialization marker when inspecting a validation failure.
        await Task.Delay(TimeSpan.FromSeconds(5));

        string stdout = await _container.GetCleanStdoutAsync();
        Assert.That(stdout, Does.Contain("Invalid range: StartBlock (10) > EndBlock (5)"));

        ExecResult ls = await _container.ExecAsync(new[] { "ls", "-1", OutputDir });
        if (ls.ExitCode == 0)
        {
            Assert.That(ls.Stdout.Trim(), Is.Empty, $"no trace files should be produced for invalid config, got: {ls.Stdout}");
        }
    }

    [Test]
    public async Task Retrospective_JsonSchema_IsWellFormed_ForCancunBlocks()
    {
        // Retrospective replay finishes before our blocks exist, so opcodeCounts is empty — schema-only check.
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

    [TestCase(true, 2, TestName = "RealTime_CapturesOpcodes_WithParallelExecution_True_2")]
    [TestCase(true, 0, TestName = "RealTime_CapturesOpcodes_WithParallelExecution_True_0")]
    [TestCase(true, -1, TestName = "RealTime_CapturesOpcodes_WithParallelExecution_True_-1")]
    [TestCase(false, 0, TestName = "RealTime_CapturesOpcodes_WithParallelExecution_False_0")]
    [TestCase(false, 2, TestName = "RealTime_CapturesOpcodes_WithParallelExecution_False_2")]
    [TestCase(false, -1, TestName = "RealTime_CapturesOpcodes_WithParallelExecution_False_-1")]
    public async Task RealTime_CapturesOpcodes_FromSubmittedTransaction(bool parallelExecution, int maxDegreeOfParallelism)
    {
        // Asserts RealTime captures PUSH1/RETURN from contract-creation txs; exercises both
        // --Blocks.ParallelExecution settings to guard the EIP-7928 parallel branch.
        const int targetBlocks = 8;
        await StartNodeAsync(PrivateMergeCommand(
            "--OpcodeTracing.Enabled", "true",
            "--OpcodeTracing.Mode", "RealTime",
            "--OpcodeTracing.StartBlock", "1",
            "--OpcodeTracing.EndBlock", targetBlocks.ToString(),
            "--OpcodeTracing.OutputDirectory", OutputDir,
            "--OpcodeTracing.MaxDegreeOfParallelism", maxDegreeOfParallelism.ToString().ToLowerInvariant(),
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
        Assert.That(sawInitCodeOpcodes, Is.True, "at least one per-block file should record init-code opcodes (PUSH1, RETURN) from the contract-creation tx");

        string cumulativeName = await WaitForCumulativeFileAsync(TimeSpan.FromSeconds(60));
        string cumulativeJson = await ReadFileAsync($"{OutputDir}/{cumulativeName}");
        TestContext.Progress.WriteLine($"=== {cumulativeName} ===\n{cumulativeJson}");
        JsonNode cumRoot = JsonNode.Parse(cumulativeJson);
        Assert.That(string.IsNullOrWhiteSpace(cumRoot["metadata"]["sessionId"].GetValue<string>()), Is.False);
        JsonObject cumulativeCounts = AssertOpcodeCountsShape(cumRoot["opcodeCounts"], requireNonEmpty: true);
        Assert.That(cumulativeCounts.ContainsKey("PUSH1"), Is.True, $"cumulative opcodeCounts should contain PUSH1 from the init-code; got {cumulativeCounts.ToJsonString()}");
        Assert.That(cumulativeCounts.ContainsKey("RETURN"), Is.True, $"cumulative opcodeCounts should contain RETURN from the init-code; got {cumulativeCounts.ToJsonString()}");

        foreach (KeyValuePair<string, long> entry in perBlockTotals)
        {
            Assert.That(cumulativeCounts.ContainsKey(entry.Key), Is.True, $"cumulative file should contain opcode '{entry.Key}' summed across per-block files");
            Assert.That(cumulativeCounts[entry.Key].GetValue<long>(), Is.GreaterThanOrEqualTo(entry.Value),
                $"cumulative count for '{entry.Key}' must be >= sum of per-block counts");
        }
    }

    [Test]
    public async Task RetrospectiveExecution_JsonSchema_IsWellFormed()
    {
        // Replay finishes before our blocks exist, so opcodeCounts is empty — schema-only check.
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

    [TestCase(true, 2, TestName = "RetrospectiveExecution_CapturesOpcodes_WithParallelExecution_True_2")]
    [TestCase(true, 0, TestName = "RetrospectiveExecution_CapturesOpcodes_WithParallelExecution_True_0")]
    [TestCase(true, -1, TestName = "RetrospectiveExecution_CapturesOpcodes_WithParallelExecution_True_-1")]
    [TestCase(false, 0, TestName = "RetrospectiveExecution_CapturesOpcodes_WithParallelExecution_False_0")]
    [TestCase(false, 2, TestName = "RetrospectiveExecution_CapturesOpcodes_WithParallelExecution_False_2")]
    [TestCase(false, -1, TestName = "RetrospectiveExecution_CapturesOpcodes_WithParallelExecution_False_-1")]
    public async Task RetrospectiveExecution_CapturesOpcodes_FromSubmittedTransaction(bool parallelExecution, int maxDegreeOfParallelism)
    {
        // Phase A produces blocks with the plugin disabled, persisting db+keystore on the host;
        // Phase B replays with RetrospectiveExecution and asserts captured opcodes.
        // Pruning.Mode None required so state survives the restart.
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
                "--Blocks.ParallelExecution", parallelExecution.ToString().ToLowerInvariant(),
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
                "--OpcodeTracing.MaxDegreeOfParallelism", maxDegreeOfParallelism.ToString().ToLowerInvariant(),
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
                Assert.That(skipped.AsArray().Count, Is.EqualTo(0), $"all blocks should be replayable; got skipped={skipped.ToJsonString()}");
            }

            JsonObject counts = AssertOpcodeCountsShape(root["opcodeCounts"], requireNonEmpty: true);
            Assert.That(counts.ContainsKey("PUSH1"), Is.True, $"opcodeCounts should contain PUSH1 from the init code; got {counts.ToJsonString()}");
            Assert.That(counts.ContainsKey("RETURN"), Is.True, $"opcodeCounts should contain RETURN from the init code; got {counts.ToJsonString()}");
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); }
            catch (Exception ex) { TestContext.Progress.WriteLine($"cleanup of {dataDir} failed: {ex.Message}"); }
            try { Directory.Delete(keystoreDir, recursive: true); }
            catch (Exception ex) { TestContext.Progress.WriteLine($"cleanup of {keystoreDir} failed: {ex.Message}"); }
        }
    }

    [TestCase(true, 2, TestName = "RetrospectiveExecution_Eip7928_Parallel_2")]
    [TestCase(true, 0, TestName = "RetrospectiveExecution_Eip7928_Parallel_0")]
    [TestCase(true, -1, TestName = "RetrospectiveExecution_Eip7928_Parallel_-1")]
    [TestCase(false, 2, TestName = "RetrospectiveExecution_Eip7928_Sequential_2")]
    [TestCase(false, 0, TestName = "RetrospectiveExecution_Eip7928_Sequential_0")]
    [TestCase(false, -1, TestName = "RetrospectiveExecution_Eip7928_Sequential_-1")]
    public async Task RetrospectiveExecution_CapturesOpcodes_OnEip7928Chain(bool blocksParallelExecution, int maxDegreeOfParallelism)
    {
        // Amsterdam-activated variant: replays under both Blocks.ParallelExecution modes to catch
        // regressions in the parallel BAL execution path (BlockAccessListManager).
        const int targetBlocks = 16;
        string dataDir = Path.Combine(Path.GetTempPath(), $"nethermind-it-data-{Guid.NewGuid():N}");
        string keystoreDir = Path.Combine(Path.GetTempPath(), $"nethermind-it-keystore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(keystoreDir);

        int blocksProduced;
        try
        {
            // Phase A — populate the chain with Amsterdam blocks.
            await StartNodeAsync(PrivateMergeCommand(
                "--OpcodeTracing.Enabled", "false",
                "--Blocks.ParallelExecution", blocksParallelExecution.ToString().ToLowerInvariant(),
                "--Pruning.Mode", "None"),
                useAmsterdamChainspec: true,
                persistentDataDirHostPath: dataDir,
                persistentKeystoreHostPath: keystoreDir);

            blocksProduced = await ProduceBlocksAsync(version: 5, count: targetBlocks, timestamp: AmsterdamTimestamp, contractCreationsToSubmit: 3);
            TestContext.Progress.WriteLine($"Phase A done: produced {blocksProduced} Amsterdam blocks");

            await _container.DisposeAsync();
            _container = null;

            // Phase B — replay with RetrospectiveExecution under the requested ParallelExecution.
            await StartNodeAsync(PrivateMergeCommand(
                "--OpcodeTracing.Enabled", "true",
                "--OpcodeTracing.Mode", "RetrospectiveExecution",
                "--OpcodeTracing.StartBlock", "1",
                "--OpcodeTracing.EndBlock", blocksProduced.ToString(),
                "--OpcodeTracing.OutputDirectory", OutputDir,
                "--OpcodeTracing.MaxDegreeOfParallelism", maxDegreeOfParallelism.ToString().ToLowerInvariant(),
                "--Blocks.ParallelExecution", blocksParallelExecution.ToString().ToLowerInvariant(),
                "--Pruning.Mode", "None"),
                useAmsterdamChainspec: true,
                persistentDataDirHostPath: dataDir,
                persistentKeystoreHostPath: keystoreDir);

            string json = await WaitForFileAsync($"{OutputDir}/opcode-trace-1-{blocksProduced}.json", TimeSpan.FromSeconds(180));
            TestContext.Progress.WriteLine($"=== opcode-trace-1-{blocksProduced}.json (RetrospectiveExecution, Blocks.ParallelExecution={blocksParallelExecution}) ===\n{json}");

            JsonNode root = JsonNode.Parse(json);
            AssertCommonMetadata(root["metadata"], expectedStart: 1, expectedEnd: blocksProduced, expectedMode: "RetrospectiveExecution", requireCompletion: true);

            JsonNode skipped = root["metadata"]["skippedBlocks"];
            if (skipped is not null)
            {
                Assert.That(skipped.AsArray().Count, Is.EqualTo(0),
                    $"all Amsterdam blocks should replay cleanly under Blocks.ParallelExecution={blocksParallelExecution}; got skipped={skipped.ToJsonString()}");
            }

            JsonObject counts = AssertOpcodeCountsShape(root["opcodeCounts"], requireNonEmpty: true);
            Assert.That(counts.ContainsKey("PUSH1"), Is.True, $"opcodeCounts should contain PUSH1 under Blocks.ParallelExecution={blocksParallelExecution}; got {counts.ToJsonString()}");
            Assert.That(counts.ContainsKey("RETURN"), Is.True, $"opcodeCounts should contain RETURN under Blocks.ParallelExecution={blocksParallelExecution}; got {counts.ToJsonString()}");

            string stdout = await _container.GetCleanStdoutAsync();
            Assert.That(stdout, Does.Not.Contain("Unhandled"), "no unhandled errors should occur during replay");
            Assert.That(stdout, Does.Not.Contain("Fatal"), "no fatal errors should occur during replay");
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); }
            catch (Exception ex) { TestContext.Progress.WriteLine($"cleanup of {dataDir} failed: {ex.Message}"); }
            try { Directory.Delete(keystoreDir, recursive: true); }
            catch (Exception ex) { TestContext.Progress.WriteLine($"cleanup of {keystoreDir} failed: {ex.Message}"); }
        }
    }

    private static void AssertCommonMetadata(JsonNode metadata, long expectedStart, long expectedEnd, string expectedMode, bool requireCompletion)
    {
        Assert.That(metadata, Is.Not.Null, "metadata must be present");
        Assert.That(metadata["startBlock"].GetValue<long>(), Is.EqualTo(expectedStart));
        Assert.That(metadata["endBlock"].GetValue<long>(), Is.EqualTo(expectedEnd));

        JsonNode mode = metadata["mode"];
        if (mode is not null)
        {
            Assert.That(mode.GetValue<string>(), Is.EqualTo(expectedMode));
        }

        JsonNode completion = metadata["completionStatus"];
        if (requireCompletion)
        {
            Assert.That(completion, Is.Not.Null, "completionStatus must be present");
            Assert.That(completion.GetValue<string>(), Is.EqualTo("complete"));
        }
        else if (completion is not null)
        {
            Assert.That(s_completionStatuses, Does.Contain(completion.GetValue<string>()));
        }

        JsonNode timestamp = metadata["timestamp"];
        if (timestamp is not null)
        {
            Assert.That(
                DateTime.TryParse(timestamp.GetValue<string>(), null, System.Globalization.DateTimeStyles.RoundtripKind, out _),
                Is.True,
                $"timestamp '{timestamp.GetValue<string>()}' must parse as ISO-8601");
        }

        JsonNode duration = metadata["duration"];
        if (duration is not null)
        {
            Assert.That(duration.GetValue<long>(), Is.GreaterThanOrEqualTo(0));
        }

        JsonNode warnings = metadata["warnings"];
        if (warnings is not null)
        {
            JsonArray warningArray = warnings.AsArray();
            foreach (JsonNode warning in warningArray)
            {
                Assert.That(warning.GetValue<string>(), Is.Not.Null);
            }
        }
    }

    private static void AssertCumulativeMetadata(JsonNode metadata, long expectedFirst, long expectedLast, bool requireCompletion)
    {
        Assert.That(metadata, Is.Not.Null, "cumulative metadata must be present");
        Assert.That(metadata["firstBlock"].GetValue<long>(), Is.EqualTo(expectedFirst));
        Assert.That(metadata["lastBlock"].GetValue<long>(), Is.EqualTo(expectedLast));
        Assert.That(metadata["totalBlocksProcessed"].GetValue<long>(), Is.GreaterThanOrEqualTo(expectedLast - expectedFirst + 1));
        Assert.That(string.IsNullOrWhiteSpace(metadata["sessionId"].GetValue<string>()), Is.False);

        JsonNode mode = metadata["mode"];
        if (mode is not null)
        {
            Assert.That(mode.GetValue<string>(), Is.EqualTo("RealTime"));
        }

        JsonNode completion = metadata["completionStatus"];
        if (requireCompletion)
        {
            Assert.That(completion, Is.Not.Null, "completionStatus must be present");
            Assert.That(completion.GetValue<string>(), Is.EqualTo("complete"));
        }

        JsonNode duration = metadata["duration"];
        if (duration is not null)
        {
            Assert.That(duration.GetValue<long>(), Is.GreaterThanOrEqualTo(0));
        }
    }

    private static void AssertPerBlockMetadata(JsonNode metadata, long expectedBlockNumber)
    {
        Assert.That(metadata, Is.Not.Null, "per-block metadata must be present");
        Assert.That(metadata["blockNumber"].GetValue<long>(), Is.EqualTo(expectedBlockNumber));

        JsonNode parentHash = metadata["parentHash"];
        if (parentHash is not null)
        {
            Assert.That(
                Regex.IsMatch(parentHash.GetValue<string>(), "^0x[0-9a-fA-F]{64}$"),
                Is.True,
                $"parentHash '{parentHash.GetValue<string>()}' must be a 32-byte hex string");
        }

        JsonNode timestamp = metadata["timestamp"];
        if (timestamp is not null)
        {
            Assert.That(timestamp.GetValue<long>(), Is.GreaterThanOrEqualTo(0));
        }

        JsonNode txCount = metadata["transactionCount"];
        if (txCount is not null)
        {
            Assert.That(txCount.GetValue<int>(), Is.GreaterThanOrEqualTo(0));
        }

        JsonNode gasUsed = metadata["gasUsed"];
        if (gasUsed is not null)
        {
            Assert.That(gasUsed.GetValue<long>(), Is.GreaterThanOrEqualTo(0));
        }

        JsonNode tracedAt = metadata["tracedAt"];
        if (tracedAt is not null)
        {
            Assert.That(
                DateTime.TryParse(tracedAt.GetValue<string>(), null, System.Globalization.DateTimeStyles.RoundtripKind, out _),
                Is.True,
                $"tracedAt '{tracedAt.GetValue<string>()}' must parse as ISO-8601");
        }
    }

    private static JsonObject AssertOpcodeCountsShape(JsonNode opcodeCounts, bool requireNonEmpty)
    {
        Assert.That(opcodeCounts, Is.Not.Null, "opcodeCounts must be present");
        JsonObject counts = opcodeCounts.AsObject();

        TestContext.Progress.WriteLine($"opcodeCounts ({counts.Count} entries): {opcodeCounts.ToJsonString()}");

        if (requireNonEmpty)
        {
            Assert.That(counts.Count, Is.GreaterThan(0),
                $"opcodeCounts must contain at least one entry; got: {opcodeCounts.ToJsonString()}");
        }

        foreach (KeyValuePair<string, JsonNode> entry in counts)
        {
            Assert.That(
                s_opcodeKeyPattern.IsMatch(entry.Key),
                Is.True,
                $"opcode key '{entry.Key}' must be either an uppercase mnemonic (e.g. ADD) or hex form (e.g. 0xfe)");
            Assert.That(entry.Value.GetValue<long>(), Is.GreaterThan(0), $"count for opcode '{entry.Key}' must be > 0");
        }

        return counts;
    }

    private async Task StartNodeAsync(string[] command, bool useTestChainspec = false, bool useAmsterdamChainspec = false, string persistentDataDirHostPath = null, string persistentKeystoreHostPath = null, bool waitForInit = true)
    {
        List<(string HostPath, string ContainerPath)> mounts = [];
        if (useTestChainspec || useAmsterdamChainspec)
        {
            string hostPath = useAmsterdamChainspec ? s_amsterdamChainspecHostPath : s_testChainspecHostPath;
            const string containerPath = "/test-sepolia.json";
            mounts.Add((hostPath, containerPath));
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
        string[] result = new string[baseArgs.Length + extra.Length];
        baseArgs.CopyTo(result, 0);
        extra.CopyTo(result, baseArgs.Length);
        return result;
    }

    private async Task<int> ProduceBlocksAsync(int version, int count, long? timestamp = null, int contractCreationsToSubmit = 0)
    {
        ExecResult jwt = await _container.ExecAsync(new[] { "cat", "jwt.hex" });
        Assert.That(jwt.ExitCode, Is.EqualTo(0));

        Uri engineUrl = new($"http://{_container.Hostname}:{_container.GetMappedPublicPort(8551)}");
        using HttpClient engineHttp = Utils.CreateEngineHttpClient(engineUrl, jwt.Stdout.Trim());

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

        // Submit txs upfront so they land in the next FCU's payload build.
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

        // Produce up to count + 5 blocks until every submitted tx is mined.
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
            Assert.That(r, Is.Not.Null, $"tx {h} should be mined within {blocksProduced} blocks");
            TestContext.Progress.WriteLine($"tx {h} mined in block {r["blockNumber"]?.GetValue<string>()}");
        }

        return blocksProduced;
    }

    private static Transaction BuildContractCreationTx(ulong nonce) => new()
    {
        Type = TxType.Legacy,
        Nonce = nonce,
        // EIP-8037 createStateCost rejects 200_000; 1_000_000 covers both pre- and post-Amsterdam.
        GasLimit = 1_000_000,
        GasPrice = 20_000_000_000UL,
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
        Assert.That(result.ExitCode, Is.EqualTo(0), $"cat {path} failed: {result.Stderr}");
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
            await Task.Delay(100);
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
            await Task.Delay(100);
        }
        throw new TimeoutException($"Cumulative opcode-trace-all-*.json did not appear within {timeout}.");
    }
}
