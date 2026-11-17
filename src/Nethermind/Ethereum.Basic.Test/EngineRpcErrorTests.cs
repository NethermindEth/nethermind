// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable annotations

using System;
using System.Collections.Generic;
using Ethereum.Test.Base;
using Nethermind.JsonRpc;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Ethereum.Basic.Test;

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

    // The converse. Every errorCode in the corpus is paired with the block exception the payload violates,
    // and a client may answer either way, so only a bare errorCode makes the RPC error mandatory.
    [TestCase(null, null, ExpectedResult = true, TestName = "Status where the fixture expects validation")]
    [TestCase(InvalidParams, null, ExpectedResult = false, TestName = "Status where the fixture demands an error and offers no exception")]
    [TestCase(InvalidParams, "BlockException.INVALID_BLOCK_ACCESS_LIST", ExpectedResult = true, TestName = "Status where the fixture also names the exception")]
    [TestCase(null, "BlockException.INVALID_BLOCK_ACCESS_LIST", ExpectedResult = true, TestName = "Status where the fixture expects rejection only")]
    public bool Payload_status_is_accepted_unless_the_fixture_demands_an_error(int? expectedErrorCode, string? validationError) =>
        BlockchainTestBase.DescribeMissingRpcError(expectedErrorCode, validationError, payloadVersion: 5) is null;

    // EEST emits errorCode as a quoted string, like newPayloadVersion.
    [TestCase("""{"errorCode": "-32602"}""", ExpectedResult = InvalidParams, TestName = "Expected error code")]
    [TestCase("""{"errorCode": null}""", ExpectedResult = null, TestName = "Explicit null")]
    [TestCase("{}", ExpectedResult = null, TestName = "Absent - the payload must be validated")]
    // Surrounding whitespace is tolerated, matching how the sibling version fields are parsed.
    [TestCase("""{"errorCode": " -32602 "}""", ExpectedResult = InvalidParams, TestName = "Padded error code")]
    public int? Fixture_error_code_is_parsed(string json) =>
        JsonToEthereumTest.ParseErrorCode(_serializer.Deserialize<TestEngineNewPayloadsJson>(json));

    // A rejection reports the error as the client gave it, its data included, so the consumer can tell the causes apart.
    [TestCase(null, ExpectedResult = "-32602: Invalid params", TestName = "No data")]
    [TestCase("missing block access list", ExpectedResult = "-32602: Invalid params: missing block access list", TestName = "String data as is")]
    public string Rpc_rejection_names_code_message_and_data(object? data) =>
        BlockchainTestBase.DescribeRpcRejection(ErrorResponse(data), InvalidParams, "Invalid params");

    [Test]
    public void Rpc_rejection_writes_structured_data_as_compact_json() =>
        Assert.That(BlockchainTestBase.DescribeRpcRejection(ErrorResponse(new Dictionary<string, string> { ["err"] = "bad" }), InvalidParams, "Invalid params"),
            Is.EqualTo("""-32602: Invalid params: {"err":"bad"}"""));

    private static JsonRpcErrorResponse ErrorResponse(object? data) =>
        new() { Error = new Error { Code = InvalidParams, Message = "Invalid params", Data = data } };

    [Test]
    public void Unparsable_error_code_is_not_silently_ignored() =>
        Assert.That(() => JsonToEthereumTest.ParseErrorCode(_serializer.Deserialize<TestEngineNewPayloadsJson>("""{"errorCode": "not-a-code"}""")),
            Throws.TypeOf<FormatException>());
}
