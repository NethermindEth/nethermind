// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Messages;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Serialization.Json;
using Nethermind.Specs.Forks;
using NUnit.Framework;
using System.Collections;
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

    [TestCaseSource(nameof(TxJsonTestCases))]
    public TxType Test_TxTypeIsDetected_ForDifferentFieldSet(string txJson)
    {
        TransactionForRpc transactionForRpc = _serializer.Deserialize<TransactionForRpc>(txJson)!;
        Result<Transaction> result = transactionForRpc.ToTransaction();
        return result.Data?.Type ?? transactionForRpc.Type ?? TxType.Legacy;
    }

    [Test]
    public void Test_TxTypeIsDeclined_WhenUnknown() => Assert.Throws<JsonException>(() => _serializer.Deserialize<TransactionForRpc>("""{"type":"0x10"}"""));

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
            yield return Make(TxType.EIP1559, """{"MaxFeePerBlobGas":"0x0"}""");
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
            yield return Make(TxType.EIP1559, """{"nonce":"0x0","to":null,"value":"0x0","maxPriorityFeePerGas":"0x0", "maxFeePerGas":"0x0","maxFeePerBlobGas":"0x0"}""");

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

            // An explicit type alone does not pick the class: a call runs on its fields, which here are none,
            // so it is defaulted like any other; the signing methods apply the type (WithRequestedType).
            yield return Make(TxType.EIP1559, """{"type":"0x0"}""");
            yield return Make(TxType.EIP1559, """{"type":"0x1"}""");
            yield return Make(TxType.EIP1559, """{"type":"0x2"}""");
            yield return Make(TxType.EIP1559, """{"type":"0x3"}""");
            yield return Make(TxType.EIP1559, """{"type":"0x4"}""");

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

    [Test]
    public void Requested_type_applies_to_a_copy()
    {
        TransactionForRpc request = _serializer.Deserialize<TransactionForRpc>("""{"type":"0x0"}""")!;

        Result<TransactionForRpc> first = request.WithRequestedType();
        Result<TransactionForRpc> second = request.WithRequestedType();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.Data, Is.TypeOf<LegacyTransactionForRpc>(), first.Error);
            Assert.That(second.Data, Is.TypeOf<LegacyTransactionForRpc>(), second.Error);
            Assert.That(request, Is.TypeOf<EIP1559TransactionForRpc>());
            Assert.That(request.ToTransaction(spec: Istanbul.Instance).Data?.Type, Is.EqualTo(TxType.Legacy));
        }
    }

    // A field its fork lacks names a type the fork doesn't enable, even when empty or zero.
    [TestCase("""{"accessList":[]}""", TestName = "Access list before Berlin")]
    [TestCase("""{"gasPrice":"0x1","accessList":[]}""", TestName = "Priced access list before Berlin")]
    [TestCase("""{"maxFeePerGas":"0x0"}""", TestName = "Dynamic fees before Berlin")]
    public void Test_FieldChosenType_IsRejectedBeforeItsFork(string txJson)
    {
        TransactionForRpc transactionForRpc = _serializer.Deserialize<TransactionForRpc>(txJson)!;
        Result<Transaction> result = transactionForRpc.ToTransaction(spec: Istanbul.Instance);
        Assert.That(result.Error, Is.EqualTo(TxErrorMessages.InvalidTxType(Istanbul.Instance.Name)));
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
    public void Test_DifferingInputAndData_Throws([ValueSource(nameof(DifferingInputAndData))] string txJson) =>
        Assert.That(() => _serializer.Deserialize<TransactionForRpc>(txJson),
            Throws.TypeOf<SafePublicMessageFormatException>().With.Message.EqualTo(RpcTransactionErrors.DataAndInputDiffer));

    [Test]
    public void Data_assignment_updates_input_outside_deserialization([Values] bool deserialized)
    {
        LegacyTransactionForRpc rpc = deserialized
            ? _serializer.Deserialize<LegacyTransactionForRpc>("""{"data":"0x6001"}""")!
            : new LegacyTransactionForRpc { Input = [0x60, 0x01] };
        byte[] data = [0x60, 0x2a];

        rpc.Data = data;

        using JsonDocument document = JsonDocument.Parse(_serializer.Serialize(rpc));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rpc.Input, Is.EqualTo(data));
            Assert.That(rpc.ToTransaction().Data!.Data, Is.SequenceEqualTo(data));
            Assert.That(document.RootElement.GetProperty("input").GetString(), Is.EqualTo("0x602a"));
            Assert.That(document.RootElement.TryGetProperty("data", out _), Is.False);
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

        Assert.That(result.IsError, Is.True);
        Assert.That(
            result.Error,
            Is.EqualTo(BlockErrorMessages.BlobGasUsedAboveBlockLimit(Cancun.Instance.GasCosts.MaxBlobGasPerBlock, 7, 7 * Eip4844Constants.GasPerBlob)));
    }
}
