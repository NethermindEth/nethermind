// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.RpcTransaction;

public static class BlobTransactionForRpcTests
{
    private static TransactionBuilder<Transaction> Build => Core.Test.Builders.Build.A.Transaction
        .WithType(TxType.Blob)
        // NOTE: We require to initialize these properties to non-null values
        .WithMaxFeePerBlobGas(UInt256.Zero)
        .WithBlobVersionedHashes([]);

    public static readonly Transaction[] Transactions =
    [
        Build.TestObject,

        Build.WithNonce(UInt256.Zero).TestObject,
        Build.WithNonce(123).TestObject,
        Build.WithNonce(UInt256.MaxValue).TestObject,

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

        Build.WithMaxFeePerBlobGas(UInt256.Zero).TestObject,
        Build.WithMaxFeePerBlobGas(123).TestObject,
        Build.WithMaxFeePerBlobGas(UInt256.MaxValue).TestObject,

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

        Build.WithBlobVersionedHashes(0).TestObject,
        Build.WithBlobVersionedHashes(1).TestObject,
        Build.WithBlobVersionedHashes(50).TestObject,

        Build.WithChainId(null).TestObject,
        Build.WithChainId(BlockchainIds.Mainnet).TestObject,
        Build.WithChainId(BlockchainIds.Sepolia).TestObject,
        Build.WithChainId(0).TestObject,
        Build.WithChainId(ulong.MaxValue).TestObject,

        Build.WithSignature(TestItem.RandomSignatureA).TestObject,
        Build.WithSignature(TestItem.RandomSignatureB).TestObject,
    ];

    public static void ValidateSchema(JsonElement json)
    {
        RpcTransactionAssertions.AssertMatchesWhenPresent(json.GetProperty("type").GetString(), "^0x3$");
        RpcTransactionAssertions.AssertMatchesWhenPresent(json.GetProperty("nonce").GetString(), "^0x([1-9a-f]+[0-9a-f]*|0)$");
        RpcTransactionAssertions.AssertMatchesWhenPresent(json.GetProperty("to").GetString(), "^0x[0-9a-fA-F]{40}$");
        RpcTransactionAssertions.AssertMatchesWhenPresent(json.GetProperty("gas").GetString(), "^0x([1-9a-f]+[0-9a-f]*|0)$");
        RpcTransactionAssertions.AssertMatchesWhenPresent(json.GetProperty("value").GetString(), "^0x([1-9a-f]+[0-9a-f]*|0)$");
        RpcTransactionAssertions.AssertMatchesWhenPresent(json.GetProperty("input").GetString(), "^0x[0-9a-f]*$");
        RpcTransactionAssertions.AssertMatchesWhenPresent(json.GetProperty("maxPriorityFeePerGas").GetString(), "^0x([1-9a-f]+[0-9a-f]*|0)$");
        RpcTransactionAssertions.AssertMatchesWhenPresent(json.GetProperty("maxFeePerGas").GetString(), "^0x([1-9a-f]+[0-9a-f]*|0)$");
        RpcTransactionAssertions.AssertMatchesWhenPresent(json.GetProperty("maxFeePerBlobGas").GetString(), "^0x([1-9a-f]+[0-9a-f]*|0)$");
        JsonElement.ArrayEnumerator accessList = json.GetProperty("accessList").EnumerateArray();
        foreach (JsonElement item in accessList)
        {
            RpcTransactionAssertions.AssertMatchesWhenPresent(item.GetProperty("address").GetString(), "^0x[0-9a-fA-F]{40}$");
            foreach (JsonElement key in item.GetProperty("storageKeys").EnumerateArray())
            {
                RpcTransactionAssertions.AssertMatchesWhenPresent(key.GetString(), "^0x[0-9a-f]{64}$");
            }
        }
        JsonElement.ArrayEnumerator blobVersionedHashes = json.GetProperty("blobVersionedHashes").EnumerateArray();
        foreach (JsonElement hash in blobVersionedHashes)
        {
            RpcTransactionAssertions.AssertMatchesWhenPresent(hash.GetString(), "^0x[0-9a-f]{64}$");
        }
        RpcTransactionAssertions.AssertMatchesWhenPresent(json.GetProperty("chainId").GetString(), "^0x([1-9a-f]+[0-9a-f]*|0)$");
        RpcTransactionAssertions.AssertMatchesWhenPresent(json.GetProperty("yParity").GetString(), "^0x([1-9a-f]+[0-9a-f]*|0)$");
        RpcTransactionAssertions.AssertMatchesWhenPresent(json.GetProperty("r").GetString(), "^0x([1-9a-f]+[0-9a-f]*|0)$");
        RpcTransactionAssertions.AssertMatchesWhenPresent(json.GetProperty("s").GetString(), "^0x([1-9a-f]+[0-9a-f]*|0)$");

        // Assert deserialization-only are not serialized
        Assert.That(json.TryGetProperty("blobs", out _), Is.False);
    }
}
