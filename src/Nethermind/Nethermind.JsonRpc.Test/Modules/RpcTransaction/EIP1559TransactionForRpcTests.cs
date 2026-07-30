// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.RpcTransaction;

public static class EIP1559TransactionForRpcTests
{
    private static TransactionBuilder<Transaction> Build => Core.Test.Builders.Build.A.Transaction.WithType(TxType.EIP1559);
    public static readonly Transaction[] Transactions =
    [
        Build.TestObject,

        Build.WithNonce(0UL).TestObject,
        Build.WithNonce(123UL).TestObject,
        Build.WithNonce(ulong.MaxValue).TestObject,

        Build.WithTo(null).TestObject,
        Build.WithTo(TestItem.AddressA).TestObject,
        Build.WithTo(TestItem.AddressB).TestObject,
        Build.WithTo(TestItem.AddressC).TestObject,
        Build.WithTo(TestItem.AddressD).TestObject,
        Build.WithTo(TestItem.AddressE).TestObject,
        Build.WithTo(TestItem.AddressF).TestObject,

        Build.WithGasLimit(0).TestObject,
        Build.WithGasLimit(123).TestObject,
        Build.WithGasLimit(long.MaxValue).TestObject,

        Build.WithValue(UInt256.Zero).TestObject,
        Build.WithValue(123).TestObject,
        Build.WithValue(UInt256.MaxValue).TestObject,

        Build.WithData(TestItem.RandomDataA).TestObject,
        Build.WithData(TestItem.RandomDataB).TestObject,
        Build.WithData(TestItem.RandomDataC).TestObject,
        Build.WithData(TestItem.RandomDataD).TestObject,

        Build.WithMaxPriorityFeePerGas(UInt256.Zero).TestObject,
        Build.WithMaxPriorityFeePerGas(123).TestObject,
        Build.WithMaxPriorityFeePerGas(UInt256.MaxValue).TestObject,

        Build.WithMaxFeePerGas(UInt256.Zero).TestObject,
        Build.WithMaxFeePerGas(123).TestObject,
        Build.WithMaxFeePerGas(UInt256.MaxValue).TestObject,

        Build.WithGasPrice(UInt256.Zero).TestObject,
        Build.WithGasPrice(123).TestObject,
        Build.WithGasPrice(UInt256.MaxValue).TestObject,

        Build.WithChainId(null).TestObject,
        Build.WithChainId(BlockchainIds.Mainnet).TestObject,
        Build.WithChainId(BlockchainIds.Sepolia).TestObject,
        Build.WithChainId(0).TestObject,
        Build.WithChainId(ulong.MaxValue).TestObject,

        Build.WithAccessList(new AccessList.Builder()
            .AddAddress(TestItem.AddressA)
            .AddStorage(1)
            .AddStorage(2)
            .AddStorage(3)
            .Build()).TestObject,

        Build.WithAccessList(new AccessList.Builder()
            .AddAddress(TestItem.AddressA)
            .AddStorage(1)
            .AddStorage(2)
            .AddAddress(TestItem.AddressB)
            .AddStorage(3)
            .Build()).TestObject,

        Build.WithSignature(TestItem.RandomSignatureA).TestObject,
        Build.WithSignature(TestItem.RandomSignatureB).TestObject,
    ];


    public static void ValidateSchema(JsonElement json)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(json.GetProperty("type").GetString(), Does.Match("^0x2$"));
            Assert.That(json.GetProperty("nonce").GetString(), Does.Match("^0x([1-9a-f]+[0-9a-f]*|0)$"));
            Assert.That(json.GetProperty("to").GetString(), Is.Null.Or.Matches("^0x[0-9a-fA-F]{40}$"));
            Assert.That(json.GetProperty("gas").GetString(), Does.Match("^0x([1-9a-f]+[0-9a-f]*|0)$"));
            Assert.That(json.GetProperty("value").GetString(), Does.Match("^0x([1-9a-f]+[0-9a-f]*|0)$"));
            Assert.That(json.GetProperty("input").GetString(), Does.Match("^0x[0-9a-f]*$"));
            Assert.That(json.GetProperty("maxPriorityFeePerGas").GetString(), Does.Match("^0x([1-9a-f]+[0-9a-f]*|0)$"));
            Assert.That(json.GetProperty("maxFeePerGas").GetString(), Does.Match("^0x([1-9a-f]+[0-9a-f]*|0)$"));
            Assert.That(json.GetProperty("gasPrice").GetString(), Does.Match("^0x([1-9a-f]+[0-9a-f]*|0)$"));
            JsonElement.ArrayEnumerator accessList = json.GetProperty("accessList").EnumerateArray();
            foreach (JsonElement item in accessList)
            {
                Assert.That(item.GetProperty("address").GetString(), Does.Match("^0x[0-9a-fA-F]{40}$"));
                foreach (JsonElement key in item.GetProperty("storageKeys").EnumerateArray())
                {
                    Assert.That(key.GetString(), Does.Match("^0x[0-9a-f]{64}$"));
                }
            }
            Assert.That(json.GetProperty("chainId").GetString(), Does.Match("^0x([1-9a-f]+[0-9a-f]*|0)$"));
            string? yParity = json.GetProperty("yParity").GetString();
            Assert.That(yParity, Does.Match("^0x([1-9a-f]+[0-9a-f]*|0)$"));
            if (json.TryGetProperty("v", out JsonElement v))
            {
                Assert.That(v.GetString(), Is.EqualTo(yParity));
            }
            Assert.That(json.GetProperty("r").GetString(), Does.Match("^0x([1-9a-f]+[0-9a-f]*|0)$"));
            Assert.That(json.GetProperty("s").GetString(), Does.Match("^0x([1-9a-f]+[0-9a-f]*|0)$"));
        }
    }
}
