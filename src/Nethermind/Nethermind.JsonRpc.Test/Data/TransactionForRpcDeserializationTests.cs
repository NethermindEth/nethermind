// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
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

    [TestCaseSource(nameof(TxJsonTestCases))]
    public TxType Test_TxTypeIsDetected_ForDifferentFieldSet(string txJson)
    {
        TransactionForRpc transactionForRpc = _serializer.Deserialize<TransactionForRpc>(txJson);
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
            yield return Make(TxType.Legacy,
                """{"nonce":"0x0","blockHash":null,"blockNumber":null,"transactionIndex":null,"to":null,"value":"0x0","gasPrice":"0x1","gas":"0x0","input":null,"maxPriorityFeePerGas":"0x1"}""");

            yield return Make(TxType.AccessList, """{"type":null,"accessList":[]}""");
            yield return Make(TxType.AccessList, """{"nonce":"0x0","to":null,"value":"0x0","accessList":[]}""");
            yield return Make(TxType.AccessList, """{"nonce":"0x0","to":null,"value":"0x0","accessList":null}""");
            yield return Make(TxType.AccessList, """{"nonce":"0x0","to":null,"value":"0x0","AccessList":null}""");

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
            yield return Make(TxType.Blob, """{"BlobVersionedHashes":null}""");
            yield return Make(TxType.Blob, """{"blobVersionedHashes":["0x01f1872d656b7a820d763e6001728b9b883f829b922089ec6ad7f5f1665470dc"]}""");

            yield return Make(TxType.SetCode, """{"nonce":"0x0","to":null,"value":"0x0","accessList":[],"authorizationList":[]}""");
            yield return Make(TxType.SetCode, """{"nonce":"0x0","to":null,"value":"0x0","maxPriorityFeePerGas":"0x0", "maxFeePerGas":"0x0","authorizationList":[]}""");
            yield return Make(TxType.SetCode, """{"authorizationList":null}""");
            yield return Make(TxType.SetCode, """{"AuthorizationList":[]}""");

            yield return Make(TxType.Legacy, """{"type":"0x0"}""");
            yield return Make(TxType.AccessList, """{"type":"0x1"}""");
            yield return Make(TxType.EIP1559, """{"type":"0x2"}""");
            yield return Make(TxType.Blob, """{"type":"0x3"}""");
            yield return Make(TxType.SetCode, """{"type":"0x4"}""");
        }
    }

    [TestCaseSource(nameof(SpecAwareResolutionCases))]
    public TxType Test_DefaultedType_IsResolvedBySpec(string txJson, IReleaseSpec? spec)
    {
        TransactionForRpc transactionForRpc = _serializer.Deserialize<TransactionForRpc>(txJson);
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

            // Defaulted type on post-Berlin → keeps EIP1559
            yield return Make(TxType.EIP1559, """{}""", Berlin.Instance);
            yield return Make(TxType.EIP1559, """{}""", London.Instance);

            // Explicit type is preserved regardless of spec
            yield return Make(TxType.EIP1559, """{"type":"0x2"}""", Istanbul.Instance);
            yield return Make(TxType.AccessList, """{"type":"0x1"}""", Istanbul.Instance);

            // Discriminator-matched type is not defaulted → preserved
            yield return Make(TxType.AccessList, """{"accessList":[]}""", Istanbul.Instance);
            yield return Make(TxType.EIP1559, """{"maxFeePerGas":"0x0"}""", Istanbul.Instance);

            // gasPrice → Legacy, not defaulted
            yield return Make(TxType.Legacy, """{"gasPrice":"0x1"}""", London.Instance);

            // No spec (null) → keeps defaulted EIP1559
            yield return Make(TxType.EIP1559, """{}""", null);
        }
    }

    [TestCase("""{"input":"0x23e52","gasPrice":"0x1"}""", TestName = "Legacy tx odd-length input")]
    [TestCase("""{"data":"0xABC","gasPrice":"0x1"}""", TestName = "Legacy tx odd-length data")]
    [TestCase("""{"input":"0x1ab"}""", TestName = "EIP1559 tx odd-length input")]
    public void Test_OddLengthInputOrData_ThrowsJsonException(string txJson) => Assert.Throws<JsonException>(() => _serializer.Deserialize<TransactionForRpc>(txJson));

    [TestCaseSource(nameof(DefaultedTypeResolutionCases))]
    public TxType Test_DefaultedType_ResolvesCorrectly(IReleaseSpec spec, bool hasAccessList)
    {
        TransactionForRpc rpc = _serializer.Deserialize<TransactionForRpc>(
            """{"to":"0x0000000000000000000000000000000000000001","data":"0x01"}""");

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
}
