// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Ethereum.Test.Base;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.State.Test.Runner.Test;

/// <summary>
/// Stdout carries nethtest's results document, so anything else written there corrupts it.
/// </summary>
/// <remarks>
/// The workflow parses the captured stdout with <c>jq</c>; when that fails it falls back to a
/// stderr-derived summary that has no per-error grouping and reports the run as a crash. The
/// post-state comparison writes its truncation notice to <see cref="Console.Out"/> once a test has
/// gathered more than eight differences, which used to corrupt the results artifact of every run
/// containing such a test. The runner is driven as a process here because the assertions inside
/// it are NUnit ones, and those mark the surrounding test failed even when the runner catches them.
/// </remarks>
[TestFixture]
public class BlockchainTestJsonOutputTests
{
    /// <summary>Enough mismatching accounts to carry the comparison past its eight-difference truncation threshold.</summary>
    private const int MismatchingAccounts = 12;

    private static readonly IJsonSerializer _serializer = new EthereumJsonSerializer();

    private string _directory = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, true);

    [Test]
    public async Task Stdout_stays_parseable_json_when_a_test_reports_more_than_8_differences()
    {
        (string stdout, string stderr) = await NethtestProcess.Run(["--blockTest", "--input", WriteFixture(), "--jsonout", "--neverTrace"]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stderr, Does.Contain("More than 8 differences"),
                "the fixture has to reach the truncation branch, otherwise this test proves nothing");
            Assert.That(() => JsonDocument.Parse(stdout).Dispose(), Throws.Nothing,
                $"stdout must stay a parseable results document, was: {Trim(stdout)}");
            Assert.That(ResultCount(stdout), Is.EqualTo(1));
        }
    }

    /// <summary>
    /// A traced run streams its JSONL trace to stderr, so the truncation notice and the failure line
    /// must stay out of it. The fixture has no blocks, so the trace is just the test-end record.
    /// </summary>
    [Test]
    public async Task Traced_run_writes_only_json_lines_to_stderr()
    {
        (string stdout, string stderr) = await NethtestProcess.Run(["--blockTest", "--input", WriteFixture(), "--trace"]);

        string[] lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(lines, Has.Length.EqualTo(1).And.All.StartsWith("{\"testEnd\":"), stderr);
            Assert.That(ResultCount(stdout), Is.EqualTo(1), "the failure is still reported in the results document");
        }
    }

    [TestCase("blocktest", "--blockTest")]
    [TestCase("enginetest", "--engineTest")]
    public async Task Each_command_runs_the_same_tests_as_its_option(string command, string option)
    {
        string fixture = command switch
        {
            "blocktest" => WriteFixture(),
            "enginetest" => WriteEngineFixture("clean_engine", []),
            _ => throw new ArgumentOutOfRangeException(nameof(command))
        };
        (string commandStdout, string commandStderr) = await RunNethtest(fixture, command);
        (string optionStdout, _) = await RunNethtest(fixture, option);

        string[] names = ResultNames(commandStdout);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(names, Has.Length.EqualTo(1), $"stderr was: {Trim(commandStderr)}");
            Assert.That(names, Is.EqualTo(ResultNames(optionStdout)));
        }
    }

    [Test]
    public async Task Version_prints_one_line_naming_the_client_and_the_tool()
    {
        (string stdout, _, int exitCode) = await NethtestProcess.RunWithExitCode(["--version"]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exitCode, Is.Zero);
            Assert.That(stdout.TrimEnd().Split('\n'), Has.Length.EqualTo(1).And.All.StartsWith("Nethermind nethtest "), stdout);
        }
    }

    [Test]
    public async Task Bal_report_prints_one_execution_line_per_block([Values] bool balReport)
    {
        (string fixture, string blockHash) = WriteOneBlockFixture();
        (string stdout, string stderr) = await RunNethtest(fixture, "blocktest", balReport ? ["--bal-report"] : []);
        string[] expected = balReport
            ? [$$"""{"event":"balExecution","block":1,"hash":"{{blockHash}}","path":"sequential","reason":"pre-amsterdam"}"""]
            : [];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ResultCount(stdout), Is.EqualTo(1), $"stdout was: {Trim(stdout)}");
            Assert.That(EventLines(stderr), Is.EqualTo(expected), $"stderr was: {Trim(stderr)}");
        }
    }

    [Test]
    public async Task Every_path_argument_runs_in_one_results_array([Values("blocktest", "enginetest")] string command)
    {
        string directory = Path.Combine(_directory, "directory");
        Directory.CreateDirectory(directory);
        File.Move(WriteOneBlockFixture().Path, Path.Combine(directory, "one_block.json"));
        (string stdout, string stderr) = await RunNethtestWithArgs(command, "--input", WriteFixture(), directory, "--jsonout", "--neverTrace");

        Assert.That(ResultNames(stdout), Is.EquivalentTo(new[] { "more_than_8_differences", "one_block" }), $"stderr was: {Trim(stderr)}");
    }

    [Test]
    public async Task Each_result_is_named_by_its_full_fixture_key([Values("blocktest", "enginetest")] string command)
    {
        string[] keys =
        [
            "tests/paris/first/test_one.py::test_same_name[fork_Paris-blockchain_test]",
            "tests/paris/second/test_two.py::test_same_name[fork_Paris-blockchain_test]",
        ];
        string fixture = command == "blocktest" ? WriteOneBlockFixture().Path : WriteEngineFixture("clean_engine", []);
        (string stdout, string stderr) = await RunNethtest(RewriteUnderKeys(fixture, keys), command);

        Assert.That(ResultNames(stdout), Is.EquivalentTo(keys), $"stderr was: {Trim(stderr)}");
    }

    [Test]
    public async Task Path_argument_after_a_fork_alias_still_runs()
    {
        (string stdout, string stderr) = await RunNethtestWithArgs("blocktest", "--forkAlias", "Unused=Prague", WriteFixture(), "--jsonout", "--neverTrace");

        Assert.That(ResultNames(stdout), Is.EquivalentTo(new[] { "more_than_8_differences" }), $"stderr was: {Trim(stderr)}");
    }

    [Test]
    public async Task A_missing_path_is_an_error_before_anything_runs([Values] bool viaInputOption)
    {
        string missing = Path.Combine(_directory, "missing.json");
        string[] args = viaInputOption
            ? ["blocktest", WriteFixture(), "--input", missing, "--jsonout", "--neverTrace"]
            : ["blocktest", WriteFixture(), missing, "--jsonout", "--neverTrace"];
        (string stdout, string stderr, int exitCode) = await NethtestProcess.RunWithExitCode(args);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exitCode, Is.EqualTo(2));
            Assert.That(stdout, Is.Empty, "no fixture may run when any path is missing");
            Assert.That(stderr, Does.Contain(missing));
        }
    }

    [Test]
    public async Task A_missing_path_on_stdin_fails_only_its_own_line()
    {
        string missing = Path.Combine(_directory, "missing.json");
        (string stdout, string stderr, int exitCode) = await NethtestProcess.RunWithExitCode(
            ["blocktest", "--stdin", "--jsonout", "--neverTrace"], $"{missing}\n{WriteFixture()}\n\n");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exitCode, Is.EqualTo(2));
            Assert.That(stderr, Does.Contain(missing));
            Assert.That(stdout, Does.Contain("more_than_8_differences"), "the line after the missing path still runs");
        }
    }

    [Test]
    [Platform(Exclude = "Win")]
    [UnsupportedOSPlatform("windows")]
    public async Task An_unreadable_fixture_file_is_an_error_before_anything_runs(
        [Values("blocktest", "enginetest")] string command, [Values] bool foundInDirectory)
    {
        string directory = Path.Combine(_directory, "walk");
        Directory.CreateDirectory(directory);
        string good = Path.Combine(directory, "good.json");
        File.Move(WriteFixture(), good);
        string unreadable = Path.Combine(directory, "unreadable.json");
        File.WriteAllText(unreadable, "{}");
        File.SetUnixFileMode(unreadable, UnixFileMode.None);
        Assume.That(() => File.OpenRead(unreadable).Dispose(), Throws.InstanceOf<UnauthorizedAccessException>(), "a superuser reads any file");

        string[] paths = foundInDirectory ? [directory] : [good, unreadable];
        (string stdout, string stderr, int exitCode) = await NethtestProcess.RunWithExitCode([command, .. paths, "--jsonout", "--neverTrace"]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exitCode, Is.EqualTo(2));
            Assert.That(stdout, Is.Empty, "no fixture may run when any file cannot be read");
            Assert.That(stderr, Does.Contain(unreadable));
        }
    }

    /// <summary>
    /// A failing fixture exits 0, which the nethtest workflow relies on; a file that cannot be loaded is named on stderr
    /// only, never in the results, and exits 2 after the other files have run and been reported.
    /// </summary>
    [Test]
    public async Task Only_a_fixture_file_that_cannot_be_loaded_changes_the_exit_status(
        [Values("blocktest", "enginetest")] string command, [Values] bool withUnloadableFile)
    {
        List<string> paths = [WriteFixture()];
        string unloadable = Path.Combine(_directory, "not_json.json");
        if (withUnloadableFile)
        {
            File.WriteAllText(unloadable, "{ not json");
            paths.Add(unloadable);
        }

        (string stdout, string stderr, int exitCode) = await NethtestProcess.RunWithExitCode([command, .. paths, "--jsonout", "--neverTrace"]);

        using JsonDocument results = JsonDocument.Parse(stdout);
        JsonElement failing = results.RootElement.EnumerateArray().Single(static r => r.GetProperty("name").GetString() == "more_than_8_differences");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(failing.GetProperty("pass").GetBoolean(), Is.False, "the fixture has to fail, otherwise this test proves nothing");
            Assert.That(results.RootElement.GetArrayLength(), Is.EqualTo(1), $"stdout was: {Trim(stdout)}");
            Assert.That(exitCode, Is.EqualTo(withUnloadableFile ? 2 : 0), $"stderr was: {Trim(stderr)}");
            if (withUnloadableFile)
                Assert.That(stderr, Does.Contain(unloadable));
        }
    }

    // State tests are outside the shared runner interface and keep their old handling of a missing path.
    [Test]
    public async Task A_missing_state_test_path_is_skipped([Values] bool viaStdin)
    {
        string missing = Path.Combine(_directory, "missing.json");
        (string stdout, string stderr, int exitCode) = viaStdin
            ? await NethtestProcess.RunWithExitCode(["--stateTest", "--stdin", "--neverTrace"], $"{missing}\n\n")
            : await NethtestProcess.RunWithExitCode(["--stateTest", "--input", missing, "--neverTrace"]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exitCode, Is.Zero, stderr);
            Assert.That(ResultCount(stdout), Is.Zero, stderr);
        }
    }

    [Test]
    public async Task A_fixture_with_no_rejected_block_reports_no_rejections([Values("blocktest", "enginetest")] string command)
    {
        string fixture = command == "blocktest" ? WriteOneBlockFixture().Path : WriteEngineFixture("clean_engine", []);
        (string stdout, string stderr) = await RunNethtest(fixture, command);

        JsonElement result = SingleResult(stdout);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("pass").GetBoolean(), Is.True, $"stderr was: {Trim(stderr)}");
            Assert.That(result.GetProperty("rejections").GetArrayLength(), Is.Zero);
        }
    }

    // The fixture names an exception other than the one nethermind reports, so the test passes only because the
    // runner leaves the reason to the consumer.
    [Test]
    public async Task Block_test_reports_each_rejected_block_with_the_client_error_and_does_not_check_it()
    {
        (string fixture, string invalidBlockHash) = WriteRejectedBlocksFixture("TransactionException.INSUFFICIENT_ACCOUNT_FUNDS");
        (string stdout, string stderr) = await RunNethtest(fixture, "blocktest");

        JsonElement result = SingleResult(stdout);
        JsonElement rejections = result.GetProperty("rejections");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("pass").GetBoolean(), Is.True, $"stdout was: {Trim(stdout)}, stderr was: {Trim(stderr)}");
            Assert.That(rejections.GetArrayLength(), Is.EqualTo(3), $"stdout was: {Trim(stdout)}");
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rejections[0].GetProperty("index").GetInt32(), Is.Zero);
            Assert.That(rejections[0].TryGetProperty("hash", out _), Is.False, "a block that does not decode has no hash");
            Assert.That(rejections[0].GetProperty("error").GetString(), Is.Not.Empty);
            Assert.That(rejections[1].GetProperty("index").GetInt32(), Is.EqualTo(1));
            Assert.That(rejections[1].GetProperty("hash").GetString(), Is.EqualTo(invalidBlockHash));
            Assert.That(rejections[1].GetProperty("error").GetString(), Does.StartWith("InvalidBlockNumber: "));
            Assert.That(rejections[2].GetProperty("index").GetInt32(), Is.EqualTo(2), "a repeated block is reported at its own index");
            Assert.That(rejections[2].GetProperty("hash").GetString(), Is.EqualTo(invalidBlockHash));
        }
    }

    [TestCase("BlockException.INVALID_BLOCK_NUMBER", ExpectedResult = true)]
    [TestCase("TransactionException.INSUFFICIENT_ACCOUNT_FUNDS", ExpectedResult = false)]
    public async Task<bool> Match_exceptions_fails_a_payload_rejected_for_another_reason(string expectedException)
    {
        (string invalidPayload, _) = SkippedNumberPayload();
        string fixture = WriteEngineFixture("rejected_engine",
        [
            $$"""{ "params": [{{invalidPayload}}], "newPayloadVersion": "1", "forkchoiceUpdatedVersion": "1", "validationError": "{{expectedException}}" }""",
        ]);
        (string stdout, _) = await RunNethtest(fixture, "enginetest", "--match-exceptions");

        return SingleResult(stdout).GetProperty("pass").GetBoolean();
    }

    // The verdict only asks whether a rejection was expected, so an exception the client error does not match passes too.
    [Test]
    public async Task Engine_test_reports_an_invalid_payload_and_a_json_rpc_error_by_payload_index(
        [Values("BlockException.INVALID_BLOCK_NUMBER", "TransactionException.INSUFFICIENT_ACCOUNT_FUNDS")] string expectedException)
    {
        (string invalidPayload, string invalidBlockHash) = SkippedNumberPayload();
        string fixture = WriteEngineFixture("rejected_engine",
        [
            $$"""{ "params": [{{invalidPayload}}], "newPayloadVersion": "1", "forkchoiceUpdatedVersion": "1", "validationError": "{{expectedException}}" }""",
            // newPayloadV3 refuses a payload without the Shanghai and Cancun fields before validating it.
            $$"""{ "params": [{{invalidPayload}}, [], "{{Keccak.Zero}}"], "newPayloadVersion": "3", "forkchoiceUpdatedVersion": "1", "errorCode": "-32602" }""",
        ]);
        (string stdout, string stderr) = await RunNethtest(fixture, "enginetest");

        JsonElement result = SingleResult(stdout);
        JsonElement rejections = result.GetProperty("rejections");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("pass").GetBoolean(), Is.True, $"stdout was: {Trim(stdout)}, stderr was: {Trim(stderr)}");
            Assert.That(rejections.GetArrayLength(), Is.EqualTo(2), $"stdout was: {Trim(stdout)}");
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rejections[0].GetProperty("index").GetInt32(), Is.Zero);
            Assert.That(rejections[0].GetProperty("hash").GetString(), Is.EqualTo(invalidBlockHash));
            Assert.That(rejections[0].GetProperty("error").GetString(), Does.StartWith("InvalidBlockNumber: "));
            Assert.That(rejections[1].GetProperty("index").GetInt32(), Is.EqualTo(1));
            Assert.That(rejections[1].TryGetProperty("hash", out _), Is.False, "a payload refused over JSON-RPC was never hashed");
            Assert.That(rejections[1].GetProperty("error").GetString(), Does.StartWith("-32602: "));
        }
    }

    // An EEST fixture whose block access list carries an entry the block never produces. The header commits to that
    // list, so it is delivered and judged during execution: the parallel executor rejects it without a sequential
    // retry, or, under --bal-sequential, the sequential executor rejects it. Under --bal-withhold the
    // block executes without it, and the list the client builds does not match the header.
    [TestCase("blocktest", null)]
    [TestCase("blocktest", "--bal-sequential")]
    [TestCase("blocktest", "--bal-withhold")]
    [TestCase("enginetest", null)]
    [TestCase("enginetest", "--bal-sequential")]
    public async Task A_corrupted_access_list_is_rejected_by_either_executor(string command, string executorFlag)
    {
        string fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", $"bal_invalid_surplus_system_address_{command}.json");
        (string stdout, string stderr) = await RunNethtest(fixture, command, executorFlag is null ? ["--bal-report"] : ["--bal-report", executorFlag]);
        string[] expectedEvents = executorFlag switch
        {
            null => ["balExecution parallel"],
            "--bal-sequential" => ["balExecution sequential disabled"],
            "--bal-withhold" => ["balExecution sequential withheld"],
            _ => throw new ArgumentOutOfRangeException(nameof(executorFlag))
        };

        JsonElement result = SingleResult(stdout);
        string[] events = Array.ConvertAll(EventLines(stderr), static line =>
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            return root.GetProperty("event").GetString() == "balExecution"
                ? $"balExecution {root.GetProperty("path").GetString()} {root.GetProperty("reason").GetString()}".TrimEnd()
                : $"balFallback {root.GetProperty("sequentialResult").GetString()}";
        });
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("pass").GetBoolean(), Is.True, $"stdout was: {Trim(stdout)}");
            Assert.That(result.GetProperty("rejections").GetArrayLength(), Is.EqualTo(1), $"stdout was: {Trim(stdout)}");
            Assert.That(events, Is.EqualTo(expectedEvents), $"stderr was: {Trim(stderr)}");
        }

        if (command == "enginetest")
        {
            Assert.That(result.GetProperty("lastPayloadStatus").GetString(), Is.EqualTo("INVALID"));
        }
    }

    // An EEST fixture whose block access list has balance, nonce, code and storage changes and storage reads. The
    // runner encodes the fixture's list itself and delivers it only when that encoding hashes to the header's
    // commitment, so the block runs in parallel only while every field kind encodes as EIP-7928 says. The fixture
    // also carries an execution witness, which is regenerated after the import and must not report the block again.
    [Test]
    public async Task An_access_list_with_every_change_kind_is_delivered()
    {
        string fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "bal_code_changes_blocktest.json");
        (string stdout, string stderr) = await RunNethtest(fixture, "blocktest", "--bal-report");

        using JsonDocument report = JsonDocument.Parse(EventLines(stderr).Single());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(SingleResult(stdout).GetProperty("pass").GetBoolean(), Is.True, $"stdout was: {Trim(stdout)}");
            Assert.That(report.RootElement.GetProperty("path").GetString(), Is.EqualTo("parallel"), $"stderr was: {Trim(stderr)}");
            Assert.That(report.RootElement.GetProperty("reason").GetString(), Is.Empty, $"stderr was: {Trim(stderr)}");
        }
    }

    /// <summary>Parses a results array holding exactly one result.</summary>
    private static JsonElement SingleResult(string stdout)
    {
        JsonElement root = JsonDocument.Parse(stdout).RootElement;
        Assert.That(root.GetArrayLength(), Is.EqualTo(1), $"stdout was: {Trim(stdout)}");
        return root[0];
    }

    private static string[] ResultNames(string stdout)
    {
        using JsonDocument document = JsonDocument.Parse(stdout);
        string[] names = new string[document.RootElement.GetArrayLength()];
        for (int i = 0; i < names.Length; i++)
        {
            names[i] = document.RootElement[i].GetProperty("name").GetString()!;
        }

        return names;
    }

    private static string[] EventLines(string stderr) =>
        Array.FindAll(stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries), static line => line.StartsWith("{\"event\""));

    private static int ResultCount(string stdout)
    {
        using JsonDocument document = JsonDocument.Parse(stdout);
        return document.RootElement.GetArrayLength();
    }

    private static string Trim(string output) => output.Length <= 200 ? output : $"{output[..200]}...";

    private static Task<(string Stdout, string Stderr)> RunNethtest(string fixture, string testType = "--blockTest", params string[] extraArgs) =>
        RunNethtestWithArgs([testType, "--input", fixture, "--jsonout", "--neverTrace", .. extraArgs]);

    private static async Task<(string Stdout, string Stderr)> RunNethtestWithArgs(params string[] args)
    {
        (string stdout, string stderr, _) = await NethtestProcess.RunWithExitCode(args);
        return (stdout, stderr);
    }

    /// <summary>
    /// Writes a fixture whose post-state expects accounts that its empty pre-state never creates, so
    /// every one of them is a difference. It declares no blocks, which keeps the head at genesis and
    /// the comparison independent of block execution.
    /// </summary>
    private string WriteFixture()
    {
        TestBlockHeaderJson genesis = GenesisHeader();
        string file = Path.Combine(_directory, "more_than_8_differences.json");
        File.WriteAllText(file, $$"""
            {
              "more_than_8_differences": {
                "network": "Berlin",
                "sealEngine": "NoProof",
                "genesisBlockHeader": {{_serializer.Serialize(genesis)}},
                "blocks": [],
                "lastblockhash": "{{genesis.Hash}}",
                "pre": {},
                "postState": {{MismatchingPostState()}}
              }
            }
            """);

        return file;
    }

    /// <summary>Rewrites a one-test fixture file so the same test appears once under each of <paramref name="keys"/>.</summary>
    private static string RewriteUnderKeys(string fixture, string[] keys)
    {
        JsonNode test = JsonNode.Parse(File.ReadAllText(fixture))!.AsObject().Single().Value!;
        JsonObject renamed = [];
        foreach (string key in keys)
        {
            renamed[key] = test.DeepClone();
        }

        File.WriteAllText(fixture, renamed.ToJsonString());
        return fixture;
    }

    /// <summary>Writes a passing Paris fixture with one empty block, which the runner executes and so reports.</summary>
    private (string Path, string BlockHash) WriteOneBlockFixture()
    {
        TestBlockHeaderJson genesis = GenesisHeader(baseFeePerGas: "0x07");
        byte[] blockRlp = Rlp.Encode(new Block(JsonToEthereumTest.Convert(ChildHeader(genesis, "0x01")))).Bytes;
        string blockHash = Rlp.Decode<Block>(blockRlp).Header.Hash!.ToString();

        string file = Path.Combine(_directory, "one_block.json");
        File.WriteAllText(file, $$"""
            {
              "one_block": {
                "network": "Paris",
                "sealEngine": "NoProof",
                "genesisBlockHeader": {{_serializer.Serialize(genesis)}},
                "blocks": [{ "rlp": "{{blockRlp.ToHexString(true)}}" }],
                "lastblockhash": "{{blockHash}}",
                "pre": {},
                "postState": {}
              }
            }
            """);

        return (file, blockHash);
    }

    /// <summary>A post-merge Paris child of <paramref name="parent"/> with the given block number.</summary>
    private static TestBlockHeaderJson ChildHeader(TestBlockHeaderJson parent, string number)
    {
        TestBlockHeaderJson child = GenesisHeader();
        child.Difficulty = "0x00";
        child.Number = number;
        child.ParentHash = parent.Hash;
        child.Timestamp = "0x0c";
        child.BaseFeePerGas = BaseFeeCalculator.Calculate(JsonToEthereumTest.Convert(parent), Paris.Instance).ToHexString(true);
        return child;
    }

    /// <summary>
    /// Writes a Paris fixture whose first block does not decode and whose second and third are one block that skips a
    /// block number, all expected to be rejected; the repeated block names <paramref name="expectException"/>.
    /// </summary>
    private (string Path, string InvalidBlockHash) WriteRejectedBlocksFixture(string expectException)
    {
        TestBlockHeaderJson genesis = GenesisHeader(baseFeePerGas: "0x07");
        // Number 2 on genesis skips a block number, which header validation rejects.
        byte[] blockRlp = Rlp.Encode(new Block(JsonToEthereumTest.Convert(ChildHeader(genesis, "0x02")))).Bytes;
        string blockHash = Rlp.Decode<Block>(blockRlp).Header.Hash!.ToString();

        string file = Path.Combine(_directory, "rejected_blocks.json");
        File.WriteAllText(file, $$"""
            {
              "rejected_blocks": {
                "network": "Paris",
                "sealEngine": "NoProof",
                "genesisBlockHeader": {{_serializer.Serialize(genesis)}},
                "blocks": [
                  { "rlp": "0xdead", "expectException": "BlockException.RLP_STRUCTURES_ENCODING" },
                  { "rlp": "{{blockRlp.ToHexString(true)}}", "expectException": "{{expectException}}" },
                  { "rlp": "{{blockRlp.ToHexString(true)}}", "expectException": "{{expectException}}" }
                ],
                "lastblockhash": "{{genesis.Hash}}",
                "pre": {},
                "postState": {}
              }
            }
            """);

        return (file, blockHash);
    }

    /// <summary>Writes a Paris engine fixture whose head stays at genesis, sending the given payload entries.</summary>
    private string WriteEngineFixture(string name, string[] payloads)
    {
        TestBlockHeaderJson genesis = GenesisHeader(baseFeePerGas: "0x07");
        genesis.Difficulty = "0x00";
        genesis.Hash = HashOf(genesis);

        string file = Path.Combine(_directory, $"{name}.json");
        File.WriteAllText(file, $$"""
            {
              "{{name}}": {
                "network": "Paris",
                "sealEngine": "NoProof",
                "genesisBlockHeader": {{_serializer.Serialize(genesis)}},
                "engineNewPayloads": [{{string.Join(",", payloads)}}],
                "lastblockhash": "{{genesis.Hash}}",
                "pre": {},
                "postState": {}
              }
            }
            """);

        return file;
    }

    /// <summary>An <c>ExecutionPayloadV1</c> on the engine fixture's genesis that skips a block number.</summary>
    private static (string Payload, string BlockHash) SkippedNumberPayload()
    {
        TestBlockHeaderJson genesis = GenesisHeader(baseFeePerGas: "0x07");
        genesis.Difficulty = "0x00";
        genesis.Hash = HashOf(genesis);
        // Number 2 on genesis skips a block number, which header validation rejects.
        TestBlockHeaderJson child = ChildHeader(genesis, "0x02");
        string blockHash = HashOf(child);

        return ($$"""
            {
              "parentHash": "{{child.ParentHash}}", "feeRecipient": "{{child.Coinbase}}", "stateRoot": "{{child.StateRoot}}",
              "receiptsRoot": "{{child.ReceiptTrie}}", "logsBloom": "{{child.Bloom}}", "prevRandao": "{{child.MixHash}}",
              "blockNumber": "{{child.Number}}", "gasLimit": "{{child.GasLimit}}", "gasUsed": "{{child.GasUsed}}",
              "timestamp": "{{child.Timestamp}}", "extraData": "{{child.ExtraData}}", "baseFeePerGas": "{{child.BaseFeePerGas}}",
              "blockHash": "{{blockHash}}", "transactions": []
            }
            """, blockHash);
    }

    private static string MismatchingPostState()
    {
        StringBuilder postState = new("{");
        for (int i = 1; i <= MismatchingAccounts; i++)
        {
            postState
                .Append(i == 1 ? "" : ",")
                .Append($$"""
                    "0x{{i:x40}}": { "balance": "0x01", "code": "0x", "nonce": "0x00", "storage": {} }
                    """);
        }

        return postState.Append('}').ToString();
    }

    private static TestBlockHeaderJson GenesisHeader(string baseFeePerGas = null)
    {
        TestBlockHeaderJson header = new()
        {
            Bloom = Bloom.Empty.Bytes.ToHexString(true),
            Coinbase = Address.Zero.ToString(),
            Difficulty = "0x020000",
            ExtraData = "0x",
            GasLimit = "0x0f4240",
            GasUsed = "0x00",
            Hash = Keccak.Zero.ToString(),
            MixHash = Keccak.Zero.ToString(),
            Nonce = "0x0000000000000000",
            Number = "0x00",
            ParentHash = Keccak.Zero.ToString(),
            ReceiptTrie = Keccak.EmptyTreeHash.ToString(),
            StateRoot = Keccak.EmptyTreeHash.ToString(),
            Timestamp = "0x00",
            TransactionsTrie = Keccak.EmptyTreeHash.ToString(),
            UncleHash = Keccak.OfAnEmptySequenceRlp.ToString(),
            BaseFeePerGas = baseFeePerGas
        };

        // The runner rejects a genesis header whose declared hash is not the one it derives, so take
        // the hash from the same encode/decode round trip it uses.
        header.Hash = HashOf(header);
        return header;
    }

    private static string HashOf(TestBlockHeaderJson header) =>
        Rlp.Decode<Block>(Rlp.Encode(new Block(JsonToEthereumTest.Convert(header))).Bytes).Header.Hash!.ToString();
}
