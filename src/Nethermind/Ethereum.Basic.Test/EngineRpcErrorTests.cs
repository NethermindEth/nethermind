// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable annotations

using System;
using System.Text.Json;
using Ethereum.Test.Base;
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

    [Test]
    public void Unparsable_error_code_is_not_silently_ignored() =>
        Assert.That(() => JsonToEthereumTest.ParseErrorCode(_serializer.Deserialize<TestEngineNewPayloadsJson>("""{"errorCode": "not-a-code"}""")),
            Throws.TypeOf<FormatException>());

    [TestCase("SlotNumberNotAfterParent: Slot number 1 must be greater than the parent's 1.", true)]
    [TestCase("SlotNumberNotAfterParent: Slot number 0 must be greater than the parent's 1.", true)]
    [TestCase("InvalidTimestamp: Timestamp in header cannot be lower than ancestor", false)]
    public void Slot_number_validation_maps_to_its_fixture_error(string validationError, bool expected) =>
        Assert.That(BlockchainTestBase.MapValidationErrorsToEestExceptions(validationError),
            expected ? Does.Contain("BlockException.INVALID_SLOT_NUMBER") : Does.Not.Contain("BlockException.INVALID_SLOT_NUMBER"));

    [TestCase("020000000000000021", "TransactionException.INVALID_CHAINID")]
    [TestCase("020000000000000022", "TransactionException.INVALID_CHAINID")]
    [TestCase("020000000000000024", "TransactionException.INVALID_SIGNATURE_VRS")]
    [TestCase("020000000000000023", "TransactionException.INVALID_SIGNATURE_VRS")]
    public void Legacy_v_overflow_maps_to_its_signature_or_chain_id_error(string v, string expected)
    {
        using JsonDocument payload = JsonDocument.Parse($$"""{"transactions":["0xd280808080808089{{v}}0101"]}""");
        string[] mapped = BlockchainTestBase.MapValidationErrorsToEestExceptions(
            "Transaction 0 is not valid: Unexpected length of integer value 9 at position 7", payload.RootElement);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mapped, Does.Contain(expected));
            Assert.That(mapped, Does.Not.Contain(expected == "TransactionException.INVALID_CHAINID"
                ? "TransactionException.INVALID_SIGNATURE_VRS"
                : "TransactionException.INVALID_CHAINID"));
        }
    }

    [TestCase("0xd2808080808080890200000000000000240101", 0, 8)] // Different field position.
    [TestCase("0xd2808080808080890200000000000000240101", 1, 7)] // Different transaction.
    [TestCase("0x02d2808080808080890200000000000000240101", 0, 7)] // Typed transaction.
    [TestCase("0xc9808080808080250101", 0, 7)] // Supported legacy v.
    public void Other_integer_errors_do_not_map_to_legacy_signature_errors(string transaction, int index, int position)
    {
        using JsonDocument payload = JsonDocument.Parse($$"""{"transactions":["{{transaction}}"]}""");
        string[] mapped = BlockchainTestBase.MapValidationErrorsToEestExceptions(
            $"Transaction {index} is not valid: Unexpected length of integer value 9 at position {position}", payload.RootElement);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mapped, Does.Not.Contain("TransactionException.INVALID_SIGNATURE_VRS"));
            Assert.That(mapped, Does.Not.Contain("TransactionException.INVALID_CHAINID"));
        }
    }

}
