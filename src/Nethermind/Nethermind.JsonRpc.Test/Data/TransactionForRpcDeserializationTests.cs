// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Messages;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Crypto;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Serialization.Json;
using Nethermind.Specs.Forks;
using NUnit.Framework;
using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace Nethermind.JsonRpc.Test.Data;

public class TransactionForRpcDeserializationTests
{
    private readonly EthereumJsonSerializer _serializer = new();

    public static readonly string[] MatchingCallData =
    [
        "\"data\":\"0x602a60005260206000f3\"",
        "\"input\":\"0x602a60005260206000f3\"",
        "\"data\":\"0x602a60005260206000f3\",\"input\":\"0x602a60005260206000f3\"",
        "\"data\":null,\"input\":\"0x602a60005260206000f3\"",
        "\"input\":null,\"data\":\"0x602a60005260206000f3\"",
    ];

    public static readonly string[] DifferingCallData =
    [
        "\"data\":\"0x602a60005260206000f3\",\"input\":\"0x600160005260206000f3\"",
        "\"input\":\"0x600160005260206000f3\",\"data\":\"0x602a60005260206000f3\"",
        "\"data\":\"0x602a60005260206000f3\",\"input\":\"0x\"",
    ];

    private static IEnumerable InvalidBlobHashFields()
    {
        string valid = "\"0x01" + new string('0', 62) + "\"";
        foreach ((string invalid, string error) in InvalidBlobHashValues())
        {
            foreach (string fields in new[]
                     {
                         $"\"blobVersionedHashes\":[{invalid}]",
                         $"\"blobVersionedHashes\":[{invalid}],\"blobVersionedHashes\":[{valid}]",
                         $"\"blobVersionedHashes\":[{invalid}],\"blobVersionedHashes\":null",
                         $"\"blobVersionedHashes\":[{valid},{invalid}]"
                     })
            {
                yield return new TestCaseData(fields, false, error);
                yield return new TestCaseData(fields, true, error);
            }
        }
    }

    private static IEnumerable<(string Value, string Error)> InvalidBlobHashValues()
    {
        foreach (string value in new[] { "\"0x\"", "\"0x0100\"", "\"0x" + new string('0', 66) + "\"",
                     "\"0x" + new string('0', 63) + "\"" })
            yield return (value, RpcTransactionErrors.InvalidBlobVersionedHashSize);
        foreach (string value in new[] { "null", "1", "\"0x\\u0030" + new string('0', 63) + "\"" })
            yield return (value, "Expected unescaped blob versioned hash hex string");
        yield return ("\"" + new string('0', 64) + "\"", Bytes.ErrMissingPrefix);
        yield return ("\"0x" + new string('z', 64) + "\"", "Invalid blob versioned hash hex");
    }

    [TestCaseSource(nameof(InvalidBlobHashFields))]
    public void Blob_hash_binding_validates_every_occurrence(string fields, bool frame, string expectedError)
    {
        string frameFields = frame ? "\"frames\":[]," : "";
        string json = $$"""{ {{frameFields}}"maxFeePerBlobGas":"0x1",{{fields}}}""";
        using (Assert.EnterMultipleScope())
        {
            AssertBlobHashBindingError<TransactionForRpc>(json, expectedError);
            AssertBlobHashBindingError<SignableTransactionForRpc>(json, expectedError);
            if (frame) AssertBlobHashBindingError<FrameTransactionForRpc>(json, expectedError);
            else AssertBlobHashBindingError<BlobTransactionForRpc>(json, expectedError);
        }
    }

    private void AssertBlobHashBindingError<T>(string json, string expectedError) =>
        Assert.That(Assert.Throws<SafePublicMessageFormatException>(() => _serializer.Deserialize<T>(json))!.Message, Is.EqualTo(expectedError));

    private static IEnumerable<TestCaseData> InvalidSegmentedBlobHashes()
    {
        foreach ((string value, string error) in InvalidBlobHashValues())
        {
            foreach (bool frame in new[] { false, true })
            {
                foreach (int split in new[] { 0, 1, 2, 33 })
                    yield return new TestCaseData(value, error, frame, split);
            }
        }
    }

    [TestCaseSource(nameof(InvalidSegmentedBlobHashes))]
    public void Blob_hash_binding_rejects_raw_grammar_across_segments(string value, string expectedError, bool frame, int split)
    {
        ReadOnlySequence<byte> sequence = BlobHashSequence(value, frame, split);
        Assert.That(Assert.Throws<SafePublicMessageFormatException>(() => DeserializeBlobHashSequence(sequence, frame))!.Message,
            Is.EqualTo(expectedError));
    }

    [Test]
    public void Blob_hash_binding_preserves_segmented_values_and_reader_position(
        [Values("0x", "0X")] string prefix, [Values] bool frame, [Values(0, 1, 2, 33)] int split)
    {
        string value = "\"" + prefix + "AB" + new string('0', 62) + "\"";
        TransactionForRpc rpc = DeserializeBlobHashSequence(BlobHashSequence(value, frame, split), frame);
        byte[][]? hashes = frame ? ((FrameTransactionForRpc)rpc).BlobVersionedHashes : ((BlobTransactionForRpc)rpc).BlobVersionedHashes;
        Assert.That(hashes, Is.EqualTo(new[] { Bytes.FromHexString("AB" + new string('0', 62)) }));
    }

    private static ReadOnlySequence<byte> BlobHashSequence(string value, bool frame, int split)
    {
        string frameFields = frame ? "\"frames\":[]," : "";
        string beforeValue = "[{" + frameFields + "\"blobVersionedHashes\":[";
        byte[] json = Encoding.UTF8.GetBytes(beforeValue + value + "]},17]");
        if (split == 0) return new(json);

        int boundary = beforeValue.Length + Math.Min(split + (value.StartsWith('"') ? 1 : 0), value.Length - 1);
        BufferSegment first = new(json.AsMemory(0, boundary));
        BufferSegment last = first.Append(json.AsMemory(boundary));
        return new(first, 0, last, last.Memory.Length);
    }

    private static TransactionForRpc DeserializeBlobHashSequence(ReadOnlySequence<byte> sequence, bool frame)
    {
        Utf8JsonReader reader = new(sequence);
        Assert.That(reader.Read(), Is.True);
        Assert.That(reader.Read(), Is.True);
        TransactionForRpc rpc = frame
            ? TypeInfoJsonSerializer.Deserialize<FrameTransactionForRpc>(ref reader, EthereumJsonSerializer.JsonOptions)!
            : TypeInfoJsonSerializer.Deserialize<BlobTransactionForRpc>(ref reader, EthereumJsonSerializer.JsonOptions)!;
        Assert.That(reader.TokenType, Is.EqualTo(JsonTokenType.EndObject));
        Assert.That(reader.Read(), Is.True);
        Assert.That(reader.GetInt32(), Is.EqualTo(17));
        Assert.That(reader.Read(), Is.True);
        Assert.That(reader.TokenType, Is.EqualTo(JsonTokenType.EndArray));
        Assert.That(reader.Read(), Is.False);
        return rpc;
    }

    private sealed class BufferSegment : ReadOnlySequenceSegment<byte>
    {
        public BufferSegment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public BufferSegment Append(ReadOnlyMemory<byte> memory)
        {
            BufferSegment next = new(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }

    [Test]
    public void Blob_hash_binding_preserves_null_empty_and_fixed_length_values(
        [Values("missing", "null", "empty", "valid", "version", "upper-prefix", "upper-hex", "cleared", "replaced")] string kind,
        [Values] bool frame)
    {
        string hash = "0x" + (kind == "version" ? "02" : "01") + new string('0', 62);
        if (kind == "upper-prefix") hash = "0X" + hash[2..];
        if (kind == "upper-hex") hash = "0xAB" + hash[4..];
        string hashes = kind == "null" ? "null" : kind == "empty" ? "[]" : $"[\"{hash}\"]";
        string duplicate = kind == "cleared" ? ",\"blobVersionedHashes\":null"
            : kind == "replaced" ? ",\"blobVersionedHashes\":[]" : "";
        string frameFields = frame ? "\"frames\":[]," : "";
        string hashFields = kind == "missing" ? "" : $",\"blobVersionedHashes\":{hashes}{duplicate}";
        TransactionForRpc rpc = _serializer.Deserialize<TransactionForRpc>(
            $$"""{ {{frameFields}}"maxFeePerBlobGas":"0x1"{{hashFields}}}""")!;
        Assert.That(rpc, Is.TypeOf(frame ? typeof(FrameTransactionForRpc) : typeof(BlobTransactionForRpc)));
        byte[][]? decodedHashes = frame ? ((FrameTransactionForRpc)rpc).BlobVersionedHashes : ((BlobTransactionForRpc)rpc).BlobVersionedHashes;

        if (kind is "missing" or "null" or "cleared") Assert.That(decodedHashes, Is.Null);
        else if (kind is "empty" or "replaced") Assert.That(decodedHashes, Is.Empty);
        else Assert.That(decodedHashes, Has.Length.EqualTo(1).And.All.Length.EqualTo(Eip4844Constants.BytesPerBlobVersionedHash));
    }

    [TestCase(EthereumEcdsaExtensions.MaxEip155ChainId, 1, false)]
    [TestCase(EthereumEcdsaExtensions.MaxEip155ChainId + 1, 0, false)]
    [TestCase(EthereumEcdsaExtensions.MaxEip155ChainId + 1, 1, true)]
    [TestCase(ulong.MaxValue, 0, true)]
    public void Protected_legacy_v_fails_where_no_eip155_v_fits_the_chain_id(ulong chainId, int yParity, bool isError)
    {
        TransactionForRpc transactionForRpc = _serializer.Deserialize<TransactionForRpc>(
            $"{{\"type\":\"0x0\",\"chainId\":\"0x{chainId:x}\",\"v\":\"0x{yParity}\",\"r\":\"0x1\",\"s\":\"0x1\"}}")!;

        Result<Transaction> result = transactionForRpc.ToTransaction();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsError, Is.EqualTo(isError));
            if (!isError) Assert.That(result.Data!.Signature!.ChainId, Is.EqualTo(chainId));
        }
    }

    [TestCaseSource(nameof(TxJsonTestCases))]
    public TxType Test_TxTypeIsDetected_ForDifferentFieldSet(string txJson)
    {
        TransactionForRpc transactionForRpc = _serializer.Deserialize<TransactionForRpc>(txJson)!;
        Result<Transaction> result = transactionForRpc.ToTransaction();
        return result.Data?.Type ?? transactionForRpc.Type ?? TxType.Legacy;
    }

    public static IEnumerable TxJsonTestCases
    {
        get
        {
            static TestCaseData Make(TxType expectedTxType, string json) => new(json) { TestName = $"Deserializes into {expectedTxType} from {json}", ExpectedResult = expectedTxType };

            yield return Make(TxType.Legacy, """{"nonce":"0x0","to":null,"value":"0x0","gasPrice":"0x0","gas":"0x0","input":null}""");
            yield return Make(TxType.Legacy, """{"nonce":"0x0","to":null,"gasPrice":"0x0","gas":"0x0","input":null}""");
            yield return Make(TxType.Legacy, """{"nonce":"0x0","to":null,"gasPrice":"0x0","gas":"0x0","input":null}""");
            yield return Make(TxType.EIP1559, """{"nonce":"0x0","input":null}""");
            yield return Make(TxType.EIP1559, """{}""");
            yield return Make(TxType.EIP1559, """{"type":null}""");
            yield return Make(TxType.EIP1559, """{"additionalField":""}""");
            // A blob fee cap names a blob transaction, which needs blob hashes, so it no longer converts without them.
            yield return Make(TxType.Blob, """{"MaxFeePerBlobGas":"0x0"}""");
            yield return Make(TxType.EIP1559,
                """{"nonce":"0x0","blockHash":null,"blockNumber":null,"transactionIndex":null,"to":null,"value":"0x0","gasPrice":"0x1","gas":"0x0","input":null,"maxPriorityFeePerGas":"0x1"}""");
            yield return Make(TxType.AccessList, """{"gasPrice":"0x1","accessList":[]}""");
            yield return Make(TxType.Blob, """{"gasPrice":"0x1","to":"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099","blobVersionedHashes":["0x01f1872d656b7a820d763e6001728b9b883f829b922089ec6ad7f5f1665470dc"]}""");
            yield return Make(TxType.SetCode, """{"gasPrice":"0x1","authorizationList":[]}""");
            yield return Make(TxType.Legacy, """{"gasPrice":"0x1","accessList":null}""");
            yield return Make(TxType.Legacy, """{"accessList":null,"gasPrice":"0x1","blobVersionedHashes":null,"authorizationList":null}""");
            yield return Make(TxType.EIP1559, """{"gasPrice":"0x1","accessList":null,"maxFeePerGas":"0x1"}""");

            yield return Make(TxType.AccessList, """{"type":null,"accessList":[]}""");
            yield return Make(TxType.AccessList, """{"nonce":"0x0","to":null,"value":"0x0","accessList":[]}""");
            yield return Make(TxType.AccessList, """{"nonce":"0x0","to":null,"value":"0x0","AccessList":[]}""");
            // An explicit null discriminator is the same as omitting it.
            yield return Make(TxType.EIP1559, """{"nonce":"0x0","to":null,"value":"0x0","accessList":null}""");
            yield return Make(TxType.EIP1559, """{"accessList":null,"blobVersionedHashes":null,"authorizationList":null}""");
            yield return Make(TxType.AccessList, """{"accessList":[],"blobVersionedHashes":null,"authorizationList":null}""");
            yield return Make(TxType.EIP1559, """{"gasPrice":null}""");
            yield return Make(TxType.EIP1559, """{"gasPrice":null,"accessList":null}""");

            yield return Make(TxType.EIP1559, """{"nonce":"0x0","to":null,"value":"0x0","accessList":[],"maxPriorityFeePerGas":"0x0"}""");
            yield return Make(TxType.EIP1559, """{"nonce":"0x0","to":null,"value":"0x0","accessList":null,"maxPriorityFeePerGas":"0x0"}""");
            yield return Make(TxType.EIP1559, """{"nonce":"0x0","to":null,"value":"0x0","maxPriorityFeePerGas":"0x0"}""");
            yield return Make(TxType.EIP1559, """{"nonce":"0x0","to":null,"value":"0x0","maxFeePerGas":"0x0"}""");
            yield return Make(TxType.EIP1559, """{"value":"0x0","maxPriorityFee":"0x0", "maxFeePerGas":"0x0"}""");
            yield return Make(TxType.EIP1559, """{"maxPriorityFeePerGas":"0x0", "maxFeePerGas":"0x0"}""");
            yield return Make(TxType.EIP1559, """{"maxFeePerGas":"0x0"}""");
            yield return Make(TxType.EIP1559, """{"maxPriorityFeePerGas":"0x0"}""");
            yield return Make(TxType.EIP1559, """{"MaxPriorityFeePerGas":"0x0"}""");
            yield return Make(TxType.Blob, """{"nonce":"0x0","to":null,"value":"0x0","maxPriorityFeePerGas":"0x0", "maxFeePerGas":"0x0","maxFeePerBlobGas":"0x0"}""");

            yield return Make(TxType.Blob, """{"nonce":"0x0","to":null,"value":"0x0","accessList":[],"blobVersionedHashes":[]}""");
            yield return Make(TxType.Blob, """{"maxFeePerBlobGas":"0x0", "blobVersionedHashes":[]}""");
            yield return Make(TxType.Blob, """{"blobVersionedHashes":[]}""");
            yield return Make(TxType.Blob, """{"BlobVersionedHashes":[]}""");
            yield return Make(TxType.EIP1559, """{"blobVersionedHashes":null}""");
            yield return Make(TxType.Blob, """{"blobVersionedHashes":["0x01f1872d656b7a820d763e6001728b9b883f829b922089ec6ad7f5f1665470dc"]}""");

            yield return Make(TxType.SetCode, """{"nonce":"0x0","to":null,"value":"0x0","accessList":[],"authorizationList":[]}""");
            yield return Make(TxType.SetCode, """{"nonce":"0x0","to":null,"value":"0x0","maxPriorityFeePerGas":"0x0", "maxFeePerGas":"0x0","authorizationList":[]}""");
            yield return Make(TxType.EIP1559, """{"authorizationList":null}""");
            yield return Make(TxType.EIP1559, """{"frames":null}""");
            yield return Make(TxType.SetCode, """{"AuthorizationList":[]}""");

            // As in geth, an explicit type alone does not pick the class: the fields do, which here are none,
            // so the request is defaulted like any other; an unknown type is ignored the same way.
            yield return Make(TxType.EIP1559, """{"type":"0x0"}""");
            yield return Make(TxType.EIP1559, """{"type":"0x1"}""");
            yield return Make(TxType.EIP1559, """{"type":"0x2"}""");
            yield return Make(TxType.EIP1559, """{"type":"0x3"}""");
            yield return Make(TxType.EIP1559, """{"type":"0x4"}""");
            yield return Make(TxType.EIP1559, """{"type":"0x7f"}""");

            string largeInput = "0x" + new string('a', 64 * 1024);
            yield return Make(TxType.EIP1559, $$"""{"type":"0x2","input":"{{largeInput}}","maxFeePerGas":"0x1"}""");
            yield return Make(TxType.Legacy, $$"""{"gasPrice":"0x1","input":"{{largeInput}}"}""");
            yield return Make(TxType.AccessList, $$"""{"accessList":[],"input":"{{largeInput}}"}""");
        }
    }

    [TestCaseSource(nameof(SpecAwareResolutionCases))]
    public TxType Test_DefaultedType_IsResolvedBySpec(string txJson, IReleaseSpec? spec)
    {
        TransactionForRpc transactionForRpc = _serializer.Deserialize<TransactionForRpc>(txJson)!;
        Result<Transaction> result = transactionForRpc.ToTransaction(spec: spec);
        Assert.That(result.IsError, Is.False, result.Error);
        return result.Data!.Type;
    }

    public static IEnumerable SpecAwareResolutionCases
    {
        get
        {
            static TestCaseData Make(TxType expected, string json, IReleaseSpec? spec) =>
                new(json, spec) { TestName = $"Resolves to {expected} from {json} on {spec?.Name ?? "null"}", ExpectedResult = expected };

            // Defaulted type on pre-Berlin (no EIP-2930) → Legacy
            yield return Make(TxType.Legacy, """{}""", Istanbul.Instance);
            yield return Make(TxType.Legacy, """{"nonce":"0x0","input":null}""", Istanbul.Instance);
            yield return Make(TxType.Legacy, """{"type":null}""", Istanbul.Instance);
            // A null discriminator is the same as omitting it, so the type stays defaulted.
            yield return Make(TxType.Legacy, """{"accessList":null}""", Istanbul.Instance);
            yield return Make(TxType.Legacy, """{"maxFeePerGas":null,"maxPriorityFeePerGas":null}""", Istanbul.Instance);

            // Defaulted type on post-Berlin → keeps EIP1559
            yield return Make(TxType.EIP1559, """{}""", Berlin.Instance);
            yield return Make(TxType.EIP1559, """{}""", London.Instance);

            // An explicit type alone is not a feature, so the call is defaulted by spec like any other
            yield return Make(TxType.Legacy, """{"type":"0x2"}""", Istanbul.Instance);
            yield return Make(TxType.Legacy, """{"type":"0x1"}""", Istanbul.Instance);

            // Discriminator-matched type is not defaulted → preserved
            yield return Make(TxType.AccessList, """{"accessList":[]}""", Berlin.Instance);
            yield return Make(TxType.AccessList, """{"gasPrice":"0x1","accessList":[]}""", London.Instance);
            yield return Make(TxType.EIP1559, """{"maxFeePerGas":"0x0"}""", London.Instance);

            // gasPrice → Legacy: defaulted, but downgrade is a no-op so result is Legacy on any spec
            yield return Make(TxType.Legacy, """{"gasPrice":"0x1"}""", London.Instance);
            yield return Make(TxType.Legacy, """{"gasPrice":"0x1"}""", Istanbul.Instance);

            // No spec (null) → keeps defaulted EIP1559
            yield return Make(TxType.EIP1559, """{}""", null);
        }
    }

    // A field its fork lacks names a type the fork doesn't enable, even when empty or zero.
    [TestCase("""{"to":"0x0000000000000000000000000000000000000001","accessList":[]}""", TestName = "Access list before Berlin")]
    [TestCase("""{"to":"0x0000000000000000000000000000000000000001","gasPrice":"0x1","accessList":[]}""", TestName = "Priced access list before Berlin")]
    [TestCase("""{"to":"0x0000000000000000000000000000000000000001","maxFeePerGas":"0x0"}""", TestName = "Dynamic fees before Berlin")]
    public void Test_FieldChosenType_IsRejectedBeforeItsFork(string txJson)
    {
        TransactionForRpc transactionForRpc = _serializer.Deserialize<TransactionForRpc>(txJson)!;
        Result<Transaction> result = transactionForRpc.ToCallTransaction(Istanbul.Instance);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Error, Is.EqualTo(TxErrorMessages.InvalidTxType(Istanbul.Instance.Name)));
            // Converting to build or sign doesn't check the fork.
            Assert.That(transactionForRpc.ToTransaction(spec: Istanbul.Instance).IsError, Is.False);
        }
    }

    // Trace-interop H14: a fee cap below the tip is wrong in any block, so it wins over the fork, even when left to execution.
    [Test]
    public void Test_FeeCapBelowTip_WinsOverTheFork([Values] bool validateFeeCapOrder)
    {
        TransactionForRpc transactionForRpc = _serializer.Deserialize<TransactionForRpc>(
            """{"to":"0x0000000000000000000000000000000000000001","maxFeePerGas":"0x0","maxPriorityFeePerGas":"0x1"}""")!;

        Result<Transaction> result = transactionForRpc.ToCallTransaction(Istanbul.Instance, validateFeeCapOrder: validateFeeCapOrder);

        Assert.That(result.Error, Is.EqualTo(RpcTransactionErrors.MaxFeePerGasSmallerThanMaxPriorityFeePerGas(0, 1)));
    }

    [TestCase("""{"input":"0x23e52","gasPrice":"0x1"}""", TestName = "Legacy tx odd-length input")]
    [TestCase("""{"data":"0xABC","gasPrice":"0x1"}""", TestName = "Legacy tx odd-length data")]
    [TestCase("""{"input":"0x1ab"}""", TestName = "EIP1559 tx odd-length input")]
    public void Test_OddLengthInputOrData_ThrowsJsonException(string txJson) => Assert.Throws<JsonException>(() => _serializer.Deserialize<TransactionForRpc>(txJson));

    [TestCase("""{"data":"0x602a","input":null}""", ExpectedResult = "0x602a")]
    [TestCase("""{"input":null,"data":"0x602a"}""", ExpectedResult = "0x602a")]
    [TestCase("""{"input":"0x602a","data":null}""", ExpectedResult = "0x602a")]
    [TestCase("""{"data":null,"input":"0x602a"}""", ExpectedResult = "0x602a")]
    [TestCase("""{"input":null}""", ExpectedResult = "0x")]
    [TestCase("""{"data":null,"input":null}""", ExpectedResult = "0x")]
    [TestCase("""{"data":"0x602a"}""", ExpectedResult = "0x602a")]
    [TestCase("""{"data":"0x602a","input":"0x602a"}""", ExpectedResult = "0x602a")]
    [TestCase("""{"input":"0x602a","data":"0x602a","gasPrice":"0x1"}""", ExpectedResult = "0x602a")]
    [TestCase("""{"data":"0x","input":""}""", ExpectedResult = "0x")]
    [TestCase("""{"input":"0x6001","input":null,"data":"0x602a"}""", ExpectedResult = "0x602a")]
    [TestCase("""{"data":"0x602a","data":null}""", ExpectedResult = "0x")]
    [TestCase("""{"data":"0x602a","INPUT":"0x6001","input":null}""", ExpectedResult = "0x602a")]
    [TestCase("""{"data":"0x602a","DATA":"0x6001"}""", ExpectedResult = "0x6001")]
    public string Test_InputDataAliasResolution(string txJson) =>
        _serializer.Deserialize<TransactionForRpc>(txJson)!.ToTransaction().Data!.Data.ToArray().ToHexString(true);

    private static readonly string[] DifferingInputAndData =
    [
        """{"data":"0x602a","input":"0x6001"}""",
        """{"input":"0x6001","data":"0x602a","gasPrice":"0x1"}""",
        """{"data":"0x602a","input":"0x"}""",
        """{"data":"0x602a","input":""}""",
        """{"input":"","data":"0x602a"}""",
        """{"type":"0x4","data":"0x602a","input":"0x6001","authorizationList":[]}""",
    ];

    [Test]
    public void Test_DifferingInputAndData_UsesInputForCallsAndRejectsSubmission([ValueSource(nameof(DifferingInputAndData))] string txJson)
    {
        using JsonDocument input = JsonDocument.Parse(txJson);
        string expected = input.RootElement.GetProperty("input").GetString()!;
        if (expected.Length == 0) expected = "0x";
        TransactionForRpc rpc = _serializer.Deserialize<TransactionForRpc>(txJson[..^1] + ",\"to\":\"0x0000000000000000000000000000000000000010\"}")!;
        Result<Transaction> call = rpc.ToCallTransaction(Prague.Instance);
        Result<Transaction> submission = rpc.ToValidatedTransaction();

        Assert.That(call.IsError, Is.False, call.Error);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(call.Data!.Data.ToArray().ToHexString(true), Is.EqualTo(expected));
            Assert.That(submission.Error, Is.EqualTo(RpcTransactionErrors.DataAndInputDiffer));
        }
    }

    [Test]
    public void Data_assignment_updates_input_outside_deserialization([Values] bool deserialized, [Values] bool assignInput)
    {
        LegacyTransactionForRpc rpc = deserialized
            ? _serializer.Deserialize<LegacyTransactionForRpc>("""{"input":"0x01","data":"0x2a"}""")!
            : new LegacyTransactionForRpc { Input = [0x60, 0x01] };
        byte[] data = [0x60, 0x2a];

        if (assignInput) rpc.Input = data;
        else rpc.Data = data;

        using JsonDocument document = JsonDocument.Parse(_serializer.Serialize(rpc));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rpc.Input, Is.EqualTo(data));
            Assert.That(rpc.ToTransaction().Data!.Data, Is.SequenceEqualTo(data));
            Assert.That(document.RootElement.GetProperty("input").GetString(), Is.EqualTo("0x602a"));
            Assert.That(document.RootElement.TryGetProperty("data", out _), Is.False);
            Assert.That(rpc.ToValidatedTransaction().Error, Is.Not.EqualTo(RpcTransactionErrors.DataAndInputDiffer));
        }

        if (assignInput) rpc.Input = null;
        else rpc.Data = null;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rpc.Input, Is.Null, "clearing calldata must not revive a JSON data alias");
            Assert.That(rpc.ToValidatedTransaction().Error, Is.EqualTo(RpcTransactionErrors.ContractCreationWithoutData));
        }
    }

    [TestCaseSource(nameof(DefaultedTypeResolutionCases))]
    public TxType Test_DefaultedType_ResolvesCorrectly(IReleaseSpec spec, bool hasAccessList)
    {
        TransactionForRpc rpc = _serializer.Deserialize<TransactionForRpc>(
            """{"to":"0x0000000000000000000000000000000000000001","data":"0x01"}""")!;

        Transaction tx = rpc.ToTransaction(spec: spec).Data!;
        Assert.That(tx.AccessList is not null, Is.EqualTo(hasAccessList));
        return tx.Type;
    }

    public static IEnumerable DefaultedTypeResolutionCases
    {
        get
        {
            yield return new TestCaseData(Istanbul.Instance, false)
                .SetName("Pre-Berlin spec resolves to Legacy without AccessList")
                .Returns(TxType.Legacy);
            yield return new TestCaseData(London.Instance, true)
                .SetName("Post-London spec resolves to EIP1559 with AccessList")
                .Returns(TxType.EIP1559);
        }
    }

    [Test]
    public void Test_BlobTransaction_WithTooManyBlobHashes_ReturnsBlobGasLimitError_WhenUserInputValidated()
    {
        TransactionForRpc rpc = _serializer.Deserialize<TransactionForRpc>(
            """{"type":"0x3","to":"0x0000000000000000000000000000000000000001","maxFeePerBlobGas":"0x1","blobVersionedHashes":["0x0100000000000000000000000000000000000000000000000000000000000000","0x0100000000000000000000000000000000000000000000000000000000000001","0x0100000000000000000000000000000000000000000000000000000000000002","0x0100000000000000000000000000000000000000000000000000000000000003","0x0100000000000000000000000000000000000000000000000000000000000004","0x0100000000000000000000000000000000000000000000000000000000000005","0x0100000000000000000000000000000000000000000000000000000000000006"]}""")!;

        Result<Transaction> result = rpc.ToTransaction(validateUserInput: true, spec: Cancun.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsError, Is.True);
            Assert.That(
                result.Error,
                Is.EqualTo(BlockErrorMessages.BlobGasUsedAboveBlockLimit(Cancun.Instance.GasCosts.MaxBlobGasPerBlock, 7, 7 * Eip4844Constants.GasPerBlob)));
        }
    }
}
