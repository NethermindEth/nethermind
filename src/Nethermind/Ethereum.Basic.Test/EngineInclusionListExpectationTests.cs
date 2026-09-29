// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable annotations

using Ethereum.Test.Base;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Ethereum.Basic.Test;

// EIP-7805 compliance is reported twice: by engine_newPayloadV6 and again by the fork-choice update to
// that head. The harness only ever asserted the first, so the fork-choice rule had no fixture coverage.
[TestFixture]
public class EngineInclusionListExpectationTests
{
    private static readonly IJsonSerializer _serializer = new EthereumJsonSerializer();

    [TestCase("{}", true, ExpectedResult = null, TestName = "Silent fixture accepts any answer")]
    [TestCase("""{"inclusionListSatisfied": true}""", true, ExpectedResult = null, TestName = "Reported value matches")]
    [TestCase("""{"inclusionListSatisfied": true}""", false, ExpectedResult = "engine_forkchoiceUpdatedV5 returned VALID and reported inclusionListSatisfied=False, expected True", TestName = "Reported value contradicts")]
    [TestCase("""{"inclusionListSatisfied": true}""", null, ExpectedResult = "engine_forkchoiceUpdatedV5 returned VALID and reported inclusionListSatisfied=null, expected True", TestName = "Field absent where one was expected")]
    [TestCase("""{"inclusionListSatisfied": false}""", true, ExpectedResult = "engine_forkchoiceUpdatedV5 returned VALID and reported inclusionListSatisfied=True, expected False", TestName = "Unsatisfied expectation contradicts")]
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

    // A version that cannot carry the field must fail rather than read as an absent one.
    [Test]
    public void Fork_choice_version_that_cannot_report_compliance_is_a_mismatch() =>
        Assert.That(BlockchainTestBase.DescribeFcuInclusionListMismatch(
                ResultWrapper<ForkchoiceUpdatedV1Result>.Success(new ForkchoiceUpdatedV1Result()),
                _serializer.Deserialize<TestEngineNewPayloadsJson>("""{"inclusionListSatisfied": true}"""), fcuVersion: 4),
            Does.StartWith("engine_forkchoiceUpdatedV4 answered with ForkchoiceUpdatedV1Result, which cannot report inclusionListSatisfied"));

    private static ResultWrapper<ForkchoiceUpdatedV2Result> ForkchoiceResponse(bool? inclusionListSatisfied, string status = PayloadStatus.Valid) =>
        ResultWrapper<ForkchoiceUpdatedV2Result>.Success(new ForkchoiceUpdatedV2Result
        {
            PayloadStatus = new PayloadStatusV2 { Status = status, InclusionListSatisfied = inclusionListSatisfied }
        });
}
