// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Ethereum.Test.Base;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Ethereum.Blockchain.Pyspec.Test;

// A JSON-RPC error from engine_newPayloadV* means the payload was refused before any consensus rule
// ran. The harness used to accept any such error as proof of a fixture's expected rejection, which
// turned whole engine suites green against a client that never validated a single payload.
[TestFixture]
public class EngineRpcErrorTests
{
    private const int UnsupportedFork = -38005;
    private const int InvalidParams = -32602;
    private const int ServerError = -32000;

    private static readonly IJsonSerializer _serializer = new EthereumJsonSerializer();

    [TestCase(UnsupportedFork, null, ExpectedResult = false, TestName = "Unsupported fork where the fixture expects validation")]
    [TestCase(InvalidParams, null, ExpectedResult = false, TestName = "Invalid params where the fixture expects validation")]
    [TestCase(ServerError, null, ExpectedResult = false, TestName = "Server error where the fixture expects validation")]
    [TestCase(InvalidParams, InvalidParams, ExpectedResult = true, TestName = "The error code the fixture asked for")]
    [TestCase(UnsupportedFork, InvalidParams, ExpectedResult = false, TestName = "An error code other than the one the fixture asked for")]
    public bool Rpc_error_is_accepted_only_when_the_fixture_asked_for_it(int errorCode, int? expectedErrorCode) =>
        BlockchainTestBase.DescribeUnexpectedRpcError(errorCode, "some message", expectedErrorCode, payloadVersion: 5) is null;

    // EEST emits errorCode as a quoted string, like newPayloadVersion.
    [TestCase("""{"errorCode": "-32602"}""", ExpectedResult = InvalidParams, TestName = "Expected error code")]
    [TestCase("""{"errorCode": null}""", ExpectedResult = null, TestName = "Explicit null")]
    [TestCase("{}", ExpectedResult = null, TestName = "Absent - the payload must be validated")]
    public int? Fixture_error_code_is_parsed(string json) =>
        JsonToEthereumTest.ParseErrorCode(_serializer.Deserialize<TestEngineNewPayloadsJson>(json));

    [Test]
    public void Unparsable_error_code_is_not_silently_ignored() =>
        Assert.That(() => JsonToEthereumTest.ParseErrorCode(_serializer.Deserialize<TestEngineNewPayloadsJson>("""{"errorCode": "not-a-code"}""")),
            Throws.TypeOf<FormatException>());
}
