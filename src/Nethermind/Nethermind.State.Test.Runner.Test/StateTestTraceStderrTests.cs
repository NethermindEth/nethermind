// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Nethermind.State.Test.Runner.Test;

/// <summary>
/// With <c>--trace</c>, stderr carries the EIP-3155 trace that differential fuzzers read line by line
/// as JSON (goevmlab's <c>--nethbatch</c> driver runs <c>nethtest -x --trace -m --stateTest</c> and
/// feeds it one fixture path per line).
/// </summary>
/// <remarks>
/// Fuzzer-generated fixtures expect a zero post-state root, so every one of them fails. The coloured
/// <c>FAIL</c> status line written after each test used to land between the traces and broke the
/// next read with <c>invalid character '\x1b'</c>.
/// </remarks>
[TestFixture]
public class StateTestTraceStderrTests
{
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
    public async Task Traced_batch_run_writes_only_json_lines_to_stderr()
    {
        string first = WriteFailingFixture("first");
        string second = WriteFailingFixture("second");

        (string stdout, string stderr) = await NethtestProcess.Run(["-x", "--trace", "-m", "--stateTest"], $"{first}\n{second}\n\n");

        string[] lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using (Assert.EnterMultipleScope())
        {
            foreach (string line in lines)
            {
                Assert.That(() => JsonDocument.Parse(line).Dispose(), Throws.Nothing, $"stderr line is not JSON: {line}");
            }

            Assert.That(lines.Count(static line => line.StartsWith("{\"stateRoot\"", StringComparison.Ordinal)), Is.EqualTo(2),
                "each fixture in the batch ends its trace with the post-state root");
            Assert.That(Results(stdout), Is.EqualTo(new[] { ("first_d0g0v0_", false), ("second_d0g0v0_", false) }),
                "each failure is still reported in the stdout results document of its batch line");
        }
    }

    [Test]
    public async Task Untraced_run_still_reports_failures_on_stderr()
    {
        (_, string stderr) = await NethtestProcess.Run(["--stateTest", "--neverTrace", "--input", WriteFailingFixture("untraced")]);

        Assert.That(stderr, Does.Contain("FAIL").And.Contain("untraced"));
    }

    /// <summary>Reads the results documents that a batch run writes to stdout, one per stdin line.</summary>
    private static List<(string Name, bool Pass)> Results(string stdout)
    {
        List<(string Name, bool Pass)> results = [];
        Utf8JsonReader reader = new(Encoding.UTF8.GetBytes(stdout), new JsonReaderOptions { AllowMultipleValues = true });
        while (reader.Read())
        {
            using JsonDocument document = JsonDocument.ParseValue(ref reader);
            foreach (JsonElement result in document.RootElement.EnumerateArray())
            {
                results.Add((result.GetProperty("name").GetString(), result.GetProperty("pass").GetBoolean()));
            }
        }

        return results;
    }

    /// <summary>A Berlin transfer to a contract that stores a value, expected to leave a zero state root.</summary>
    private string WriteFailingFixture(string name)
    {
        string file = Path.Combine(_directory, $"{name}.json");
        File.WriteAllText(file, $$"""
            {
              "{{name}}": {
                "env": {
                  "currentCoinbase": "0x2adc25665018aa1fe0e6bc666dac8fc2697ff9ba",
                  "currentDifficulty": "0x020000",
                  "currentGasLimit": "0x0f4240",
                  "currentNumber": "0x01",
                  "currentTimestamp": "0x03e8",
                  "previousHash": "0x0000000000000000000000000000000000000000000000000000000000000000"
                },
                "pre": {
                  "0xa94f5374fce5edbc8e2a8697c15331677e6ebf0b": { "balance": "0x0de0b6b3a7640000", "code": "0x", "nonce": "0x00", "storage": {} },
                  "0x1000000000000000000000000000000000000000": { "balance": "0x00", "code": "0x600160005500", "nonce": "0x00", "storage": {} }
                },
                "transaction": {
                  "data": ["0x"],
                  "gasLimit": ["0x0186a0"],
                  "gasPrice": "0x0a",
                  "nonce": "0x00",
                  "secretKey": "0x45a915e4d060149eb4365960e6a7a45f334393093061116b197e3240065ff2d8",
                  "to": "0x1000000000000000000000000000000000000000",
                  "value": ["0x00"]
                },
                "post": {
                  "Berlin": [
                    {
                      "hash": "0x0000000000000000000000000000000000000000000000000000000000000000",
                      "logs": "0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347",
                      "indexes": { "data": 0, "gas": 0, "value": 0 }
                    }
                  ]
                }
              }
            }
            """);

        return file;
    }
}
