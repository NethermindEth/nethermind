// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable annotations

using System;
using Ethereum.Test.Base;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Ethereum.Basic.Test;

// EIP-7805 compliance is reported twice: by engine_newPayloadV6 and again by the fork-choice update to
// that head. The harness only ever asserted the first, so the fork-choice rule had no fixture coverage.
[TestFixture]
public class EngineInclusionListExpectationTests
{
    private const string Unasserted = "unasserted";

    private static readonly IJsonSerializer _serializer = new EthereumJsonSerializer();

    [TestCase("{}", ExpectedResult = Unasserted, TestName = "Silent fixture leaves the fork-choice response unasserted")]
    [TestCase("""{"newPayloadVersion": "6"}""", ExpectedResult = Unasserted, TestName = "Pre-EIP-7805 fixture is unaffected")]
    // execution-apis bogota.md: a head just deemed VALID by newPayload must repeat its compliance answer.
    [TestCase("""{"inclusionListSatisfied": true}""", ExpectedResult = "True", TestName = "Satisfied payload is inherited")]
    [TestCase("""{"inclusionListSatisfied": false}""", ExpectedResult = "False", TestName = "Unsatisfied payload is inherited")]
    [TestCase("""{"inclusionListSatisfied": true, "forkchoiceUpdatedInclusionListSatisfied": false}""", ExpectedResult = "False", TestName = "Override wins over the inherited value")]
    [TestCase("""{"inclusionListSatisfied": true, "forkchoiceUpdatedInclusionListSatisfied": null}""", ExpectedResult = "null", TestName = "Explicit null demands an absent field")]
    [TestCase("""{"forkchoiceUpdatedInclusionListSatisfied": true}""", ExpectedResult = "True", TestName = "Override alone")]
    public string Fixture_forkchoice_expectation_is_parsed(string json)
    {
        TestEngineNewPayloadsJson enginePayload = _serializer.Deserialize<TestEngineNewPayloadsJson>(json);
        return JsonToEthereumTest.TryParseForkchoiceInclusionListSatisfied(enginePayload, out bool? expected)
            ? expected?.ToString() ?? "null"
            : Unasserted;
    }

    [Test]
    public void Unparsable_forkchoice_expectation_is_not_silently_ignored() =>
        Assert.That(() => JsonToEthereumTest.TryParseForkchoiceInclusionListSatisfied(
                _serializer.Deserialize<TestEngineNewPayloadsJson>("""{"forkchoiceUpdatedInclusionListSatisfied": "yes"}"""), out _),
            Throws.TypeOf<FormatException>());
}
