// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable annotations

using Ethereum.Test.Base;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Ethereum.Basic.Test;

// EIP-7805 inclusion is checked apart from execution: engine_newPayloadV6 returns the check's identifier, and a
// fixture's inclusionListSatisfied is the expectation for the verdict that check completes with.
[TestFixture]
public class EngineInclusionListExpectationTests
{
    private static readonly IJsonSerializer _serializer = new EthereumJsonSerializer();

    [TestCase("{}", true, ExpectedResult = null, TestName = "Silent fixture accepts any verdict")]
    [TestCase("{}", null, ExpectedResult = null, TestName = "Silent fixture accepts a pending check")]
    [TestCase("""{"inclusionListSatisfied": true}""", true, ExpectedResult = null, TestName = "Verdict matches")]
    [TestCase("""{"inclusionListSatisfied": true}""", false, ExpectedResult = "engine_newPayloadV6 inclusion check reported inclusionListSatisfied=False, expected True", TestName = "Verdict contradicts")]
    [TestCase("""{"inclusionListSatisfied": false}""", true, ExpectedResult = "engine_newPayloadV6 inclusion check reported inclusionListSatisfied=True, expected False", TestName = "Unsatisfied expectation contradicts")]
    [TestCase("""{"inclusionListSatisfied": false}""", null, ExpectedResult = "engine_newPayloadV6 inclusion check reported inclusionListSatisfied=null (still pending), expected False", TestName = "Pending check is never taken as either verdict")]
    public string? Inclusion_verdict_is_checked_against_the_fixture(string json, bool? verdict) =>
        BlockchainTestBase.DescribeInclusionVerdictMismatch(_serializer.Deserialize<TestEngineNewPayloadsJson>(json), verdict, payloadVersion: 6);
}
