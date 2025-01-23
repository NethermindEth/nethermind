// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Serialization.Json;
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
        return transactionForRpc.ToTransaction().Type;
    }

    [Test]
    public void Test_TxTypeIsDeclined_WhenUnknown()
    {
        Assert.Throws<JsonException>(() => _serializer.Deserialize<TransactionForRpc>("""{"type":"0x10"}"""));
    }

    public static IEnumerable TxJsonTestCases
    {
        get
        {
            static TestCaseData Make(TxType expectedTxType, string json) => new(json) { TestName = $"Deserilizes into {expectedTxType} from {json}", ExpectedResult = expectedTxType };

            yield return Make(TxType.Legacy, """{"nonce":"0x0","to":null,"value":"0x0","gasPrice":"0x0","gas":"0x0","input":null}""");
            yield return Make(TxType.Legacy, """{"nonce":"0x0","to":null,"gasPrice":"0x0","gas":"0x0","input":null}""");
            yield return Make(TxType.Legacy, """{"nonce":"0x0","to":null,"gasPrice":"0x0","gas":"0x0","input":null}""");
            yield return Make(TxType.Legacy, """{"nonce":"0x0","input":null}""");
            yield return Make(TxType.Legacy, """{}""");
            yield return Make(TxType.Legacy, """{"type":null}""");
            yield return Make(TxType.Legacy, """{"additionalField":""}""");

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

            yield return Make(TxType.Blob, """{"nonce":"0x0","to":null,"value":"0x0","accessList":[],"maxFeePerBlobGas":"0x0"}""");
            yield return Make(TxType.Blob, """{"nonce":"0x0","to":null,"value":"0x0","maxPriorityFeePerGas":"0x0", "maxFeePerGas":"0x0","maxFeePerBlobGas":"0x0"}""");
            yield return Make(TxType.Blob, """{"maxFeePerBlobGas":"0x0", "blobVersionedHashes":[]}""");
            yield return Make(TxType.Blob, """{"MaxFeePerBlobGas":"0x0"}""");
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
}
