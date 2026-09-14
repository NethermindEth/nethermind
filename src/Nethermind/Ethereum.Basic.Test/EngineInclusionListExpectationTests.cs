// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable annotations

using System;
using Ethereum.Test.Base;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Ethereum.Basic.Test;

// EIP-7805 compliance is reported twice: by engine_newPayloadV6 and again by the fork-choice update to
// that head. The harness only ever asserted the first, so the fork-choice rule had no fixture coverage.
// The assertion counter is process-wide, so these cases measure it one at a time.
[TestFixture]
[NonParallelizable]
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

    [TestCase("{}", true, ExpectedResult = null, TestName = "Silent fixture accepts any answer")]
    [TestCase("""{"inclusionListSatisfied": true}""", true, ExpectedResult = null, TestName = "Reported value matches")]
    [TestCase("""{"inclusionListSatisfied": true}""", false, ExpectedResult = "engine_forkchoiceUpdatedV5 returned VALID and reported inclusionListSatisfied=False, expected True", TestName = "Reported value contradicts")]
    [TestCase("""{"inclusionListSatisfied": true}""", null, ExpectedResult = "engine_forkchoiceUpdatedV5 returned VALID and reported inclusionListSatisfied=null, expected True", TestName = "Field absent where one was expected")]
    [TestCase("""{"inclusionListSatisfied": true, "forkchoiceUpdatedInclusionListSatisfied": null}""", null, ExpectedResult = null, TestName = "Field absent as demanded")]
    public string? Forkchoice_response_is_checked_against_the_fixture(string json, bool? reported) =>
        BlockchainTestBase.DescribeFcuInclusionListMismatch(ForkchoiceResponse(reported), _serializer.Deserialize<TestEngineNewPayloadsJson>(json), fcuVersion: 5);

    // A head that never became VALID reports no compliance either, so the message must not read as an
    // EIP-7805 contradiction.
    [Test]
    public void Non_valid_head_is_named_rather_than_blamed_on_the_inclusion_list() =>
        Assert.That(BlockchainTestBase.DescribeFcuInclusionListMismatch(
                ForkchoiceResponse(null, PayloadStatus.Syncing),
                _serializer.Deserialize<TestEngineNewPayloadsJson>("""{"inclusionListSatisfied": true}"""), fcuVersion: 5),
            Is.EqualTo("engine_forkchoiceUpdatedV5 returned SYNCING and reported inclusionListSatisfied=null, expected True"));

    // A version that cannot carry the field must fail rather than read as an absent one, or a null
    // expectation would pass vacuously while counting as a check that ran.
    [TestCase("""{"inclusionListSatisfied": true}""", TestName = "Boolean expectation against an older version")]
    [TestCase("""{"forkchoiceUpdatedInclusionListSatisfied": null}""", TestName = "Null expectation against an older version")]
    public void Fork_choice_version_that_cannot_report_compliance_is_a_mismatch(string json) =>
        Assert.That(BlockchainTestBase.DescribeFcuInclusionListMismatch(
                ResultWrapper<ForkchoiceUpdatedV1Result>.Success(new ForkchoiceUpdatedV1Result()),
                _serializer.Deserialize<TestEngineNewPayloadsJson>(json), fcuVersion: 4),
            Does.StartWith("engine_forkchoiceUpdatedV4 answered with ForkchoiceUpdatedV1Result, which cannot report inclusionListSatisfied"));

    // A release that dropped the field would leave the rule uncovered with every test still green, so the
    // FOCIL lane gates on how many checks actually ran.
    [TestCase("{}", ExpectedResult = 0, TestName = "Silent fixture is not counted")]
    [TestCase("""{"newPayloadVersion": "6"}""", ExpectedResult = 0, TestName = "Pre-EIP-7805 fixture is not counted")]
    [TestCase("""{"inclusionListSatisfied": true}""", ExpectedResult = 1, TestName = "Inherited expectation is counted")]
    [TestCase("""{"forkchoiceUpdatedInclusionListSatisfied": null}""", ExpectedResult = 1, TestName = "Absent-field expectation is counted")]
    public long Only_the_checks_that_run_are_counted(string json)
    {
        long before = BlockchainTestBase.FcuInclusionListAssertionCount;
        BlockchainTestBase.DescribeFcuInclusionListMismatch(ForkchoiceResponse(true), _serializer.Deserialize<TestEngineNewPayloadsJson>(json), fcuVersion: 5);
        return BlockchainTestBase.FcuInclusionListAssertionCount - before;
    }

    private static ResultWrapper<ForkchoiceUpdatedV2Result> ForkchoiceResponse(bool? inclusionListSatisfied, string status = PayloadStatus.Valid) =>
        ResultWrapper<ForkchoiceUpdatedV2Result>.Success(new ForkchoiceUpdatedV2Result
        {
            PayloadStatus = new PayloadStatusV2 { Status = status, InclusionListSatisfied = inclusionListSatisfied }
        });
}
