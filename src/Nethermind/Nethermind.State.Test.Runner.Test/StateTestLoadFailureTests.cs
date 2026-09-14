// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Ethereum.Test.Base;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Test.Runner;
using NUnit.Framework;

namespace Nethermind.State.Test.Runner.Test;

/// <summary>
/// How <c>nethtest --stateTest</c> treats a fixture the loader struggles with: one that cannot be
/// parsed must be reported as a failure rather than vanish from the run, which previously left an
/// empty result array, and one that merely pins its own signature must load and run.
/// </summary>
public class StateTestLoadFailureTests
{
    private const string Sender = "0xa94f5374fce5edbc8e2a8697c15331677e6ebf0b";

    /// <summary>One past the largest <c>r</c> or <c>s</c> a valid secp256k1 signature can carry.</summary>
    private static readonly UInt256 Secp256k1N =
        UInt256.Parse("115792089237316195423570985008687907852837564279074904382605163141518161494337");

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
    // failures were reported the whole fixture just vanished from the run. The two cases are the two
    // branches such a fixture can take, since whether the pinned signature survives decoding decides
    // which transaction the runner ends up with.
    [Test]
    public void Fixture_without_a_secret_key_loads([Values] bool signatureSurvivesDecoding)
    {
        // An out-of-range r is one of the signatures bad_v_r_s pins and it still decodes, so the
        // fixture's own transaction reaches the runner and tx validation is what rejects it. A v
        // below 27 does not decode, and the template standing in for it must stay invalid rather
        // than become a valid transfer from the named sender.
        string post = signatureSurvivesDecoding
            ? $$"""
                "expectException": "TransactionException.INVALID_SIGNATURE_VRS",
                      "txbytes": "{{PinnedOutOfRangeSignatureTxBytes()}}",
                """
            : string.Empty;

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
                  "sender": "{{Sender}}",
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
                      {{post}}
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
            Assert.That(tests[0].Transaction.SenderAddress,
                Is.EqualTo(signatureSurvivesDecoding ? new Address(Sender) : Address.Zero));
            Assert.That(new UInt256(tests[0].Transaction.Signature!.RAsSpan, true),
                Is.EqualTo(signatureSurvivesDecoding ? Secp256k1N : UInt256.One),
                "the fixture's own signature reaches the runner only when it decodes");
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

    /// <summary>
    /// A legacy transaction carrying an out-of-range <c>r</c>. The RLP decoder range-checks only
    /// <c>v</c>, so this signature reaches the runner intact and transaction validation is what
    /// rejects it - unlike a <c>v</c> below 27, which the decoder refuses.
    /// </summary>
    private static string PinnedOutOfRangeSignatureTxBytes()
    {
        Transaction transaction = new()
        {
            Nonce = 0,
            GasPrice = 10,
            GasLimit = 21000,
            To = new Address("0x1000000000000000000000000000000000000000"),
            Value = 1,
            Signature = new Signature(Secp256k1N, UInt256.One, 27)
        };

        return Rlp.Encode(transaction, RlpBehaviors.SkipTypedWrapping).Bytes.ToHexString(true);
    }
}
