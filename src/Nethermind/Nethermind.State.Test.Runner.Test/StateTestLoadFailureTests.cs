// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Ethereum.Test.Base;
using Nethermind.Core;
using Nethermind.Test.Runner;
using NUnit.Framework;

namespace Nethermind.State.Test.Runner.Test;

/// <summary>
/// A state-test fixture that cannot be parsed must be reported as a failure rather than vanish
/// from the run, which previously left <c>nethtest --stateTest</c> printing an empty result array.
/// </summary>
public class StateTestLoadFailureTests
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

    [TestCase("{\"broken\": ", nameof(JsonException), TestName = "Malformed JSON")]
    [TestCase("{\"t\":{\"transaction\":{\"value\":[\"0x\"]}}}", nameof(JsonException), TestName = "Deserializer rejects a quantity")]
    [TestCase("{\"t\":{\"env\":{},\"pre\":{},\"transaction\":{}}}", nameof(NullReferenceException), TestName = "Conversion to a test case throws")]
    public void Fixture_that_fails_to_parse_survives_the_state_test_type_filter(string content, string expectedException)
    {
        string file = Path.Combine(_directory, "fixture.json");
        File.WriteAllText(file, content);

        List<GeneralStateTest> tests = [.. new TestsSourceLoader(new LoadGeneralStateTestFileStrategy(), file).LoadTests<GeneralStateTest>()];

        Assert.That(tests, Has.Count.EqualTo(1));
        Assert.That(tests[0].Name, Is.EqualTo(file));
        Assert.That(tests[0].LoadFailure, Does.Contain(expectedException));
    }

    // frontier/validation/transaction/bad_v_r_s pins an explicit v/r/s, so it carries no secretKey the
    // loader could sign with. Building a private key out of the missing one threw, and until load
    // failures were reported the whole fixture just vanished from the run.
    [Test]
    public void Fixture_without_a_secret_key_loads_as_an_intentionally_invalid_transaction()
    {
        const string sender = "0xa94f5374fce5edbc8e2a8697c15331677e6ebf0b";
        string file = Path.Combine(_directory, "bad_v_r_s.json");
        File.WriteAllText(file, $$"""
            {
              "t": {
                "env": {
                  "currentCoinbase": "0x2adc25665018aa1fe0e6bc666dac8fc2697ff9ba",
                  "currentDifficulty": "0x20000",
                  "currentGasLimit": "0x0f4240",
                  "currentNumber": "0x01",
                  "currentTimestamp": "0x03e8",
                  "previousHash": "0x0000000000000000000000000000000000000000000000000000000000000000"
                },
                "pre": {},
                "transaction": {
                  "sender": "{{sender}}",
                  "nonce": "0x00",
                  "gasPrice": "0x0a",
                  "gasLimit": ["0x5208"],
                  "to": "0x1000000000000000000000000000000000000000",
                  "value": ["0x01"],
                  "data": ["0x"]
                },
                "post": {
                  "Frontier": [
                    {
                      "hash": "0x0000000000000000000000000000000000000000000000000000000000000000",
                      "logs": "0x0000000000000000000000000000000000000000000000000000000000000000",
                      "indexes": { "data": 0, "gas": 0, "value": 0 }
                    }
                  ]
                }
              }
            }
            """);

        List<GeneralStateTest> tests = [.. new TestsSourceLoader(new LoadGeneralStateTestFileStrategy(), file).LoadTests<GeneralStateTest>()];

        Assert.That(tests, Has.Count.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tests[0].LoadFailure, Is.Null);
            Assert.That(tests[0].Transaction.SenderAddress, Is.EqualTo(Address.Zero),
                "an unsignable template must stay invalid rather than become a valid transfer from the named sender");
        }
    }

    [Test]
    public void Runner_reports_a_load_failure_instead_of_running_the_test()
    {
        StateTestsRunner runner = new(WhenTrace.Never, traceMemory: false, traceStack: false, chainId: BlockchainIds.Mainnet, suppressOutput: true);
        GeneralStateTest test = new() { Name = "fixture.json", LoadFailure = "Failed to load: boom" };

        EthereumTestResult result = runner.RunSingleTest(test);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Pass, Is.False);
            Assert.That(result.Error, Is.EqualTo("Failed to load: boom"));
        }
    }
}
