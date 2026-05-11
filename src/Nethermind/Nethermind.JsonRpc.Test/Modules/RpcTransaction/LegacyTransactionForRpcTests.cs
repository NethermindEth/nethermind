// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.RpcTransaction;

public static class LegacyTransactionForRpcTests
{
    private static TransactionBuilder<Transaction> BuildALegacyTransaction => Build.A.Transaction.WithType(TxType.Legacy);
    public static readonly Transaction[] Transactions =
    [
        BuildALegacyTransaction.TestObject,

        BuildALegacyTransaction.WithNonce(UInt256.Zero).TestObject,
        BuildALegacyTransaction.WithNonce((UInt256)123).TestObject,
        BuildALegacyTransaction.WithNonce(UInt256.MaxValue).TestObject,

        BuildALegacyTransaction.WithTo(null).TestObject,
        BuildALegacyTransaction.WithTo(TestItem.AddressA).TestObject,
        BuildALegacyTransaction.WithTo(TestItem.AddressB).TestObject,
        BuildALegacyTransaction.WithTo(TestItem.AddressC).TestObject,
        BuildALegacyTransaction.WithTo(TestItem.AddressD).TestObject,
        BuildALegacyTransaction.WithTo(TestItem.AddressE).TestObject,
        BuildALegacyTransaction.WithTo(TestItem.AddressF).TestObject,

        BuildALegacyTransaction.WithGasLimit(0).TestObject,
        BuildALegacyTransaction.WithGasLimit(123).TestObject,
        BuildALegacyTransaction.WithGasLimit(long.MaxValue).TestObject,

        BuildALegacyTransaction.WithValue(UInt256.Zero).TestObject,
        BuildALegacyTransaction.WithValue((UInt256)123).TestObject,
        BuildALegacyTransaction.WithValue(UInt256.MaxValue).TestObject,

        BuildALegacyTransaction.WithData(TestItem.RandomDataA).TestObject,
        BuildALegacyTransaction.WithData(TestItem.RandomDataB).TestObject,
        BuildALegacyTransaction.WithData(TestItem.RandomDataC).TestObject,
        BuildALegacyTransaction.WithData(TestItem.RandomDataD).TestObject,

        BuildALegacyTransaction.WithGasPrice(UInt256.Zero).TestObject,
        BuildALegacyTransaction.WithGasPrice((UInt256)123).TestObject,
        BuildALegacyTransaction.WithGasPrice(UInt256.MaxValue).TestObject,

        BuildALegacyTransaction.WithChainId(null).TestObject,
        BuildALegacyTransaction.WithChainId(BlockchainIds.Mainnet).TestObject,
        BuildALegacyTransaction.WithChainId(BlockchainIds.Sepolia).TestObject,
        BuildALegacyTransaction.WithChainId(0).TestObject,
        BuildALegacyTransaction.WithChainId(ulong.MaxValue).TestObject,

        BuildALegacyTransaction.WithSignature(TestItem.RandomSignatureA).TestObject,
        BuildALegacyTransaction.WithSignature(TestItem.RandomSignatureB).TestObject,
    ];

    public static void ValidateSchema(JsonElement json)
    {
        RpcTransactionAssertions.AssertMatchesWhenPresent(json.GetProperty("type").GetString(), "^0x0$");
        RpcTransactionAssertions.AssertMatchesWhenPresent(json.GetProperty("nonce").GetString(), "^0x([1-9a-f]+[0-9a-f]*|0)$");
        RpcTransactionAssertions.AssertMatchesWhenPresent(json.GetProperty("to").GetString(), "^0x[0-9a-fA-F]{40}$");
        RpcTransactionAssertions.AssertMatchesWhenPresent(json.GetProperty("from").GetString(), "^0x[0-9a-fA-F]{40}$");
        RpcTransactionAssertions.AssertMatchesWhenPresent(json.GetProperty("gas").GetString(), "^0x([1-9a-f]+[0-9a-f]*|0)$");
        RpcTransactionAssertions.AssertMatchesWhenPresent(json.GetProperty("value").GetString(), "^0x([1-9a-f]+[0-9a-f]*|0)$");
        RpcTransactionAssertions.AssertMatchesWhenPresent(json.GetProperty("input").GetString(), "^0x[0-9a-f]*$");
        RpcTransactionAssertions.AssertMatchesWhenPresent(json.GetProperty("gasPrice").GetString(), "^0x([1-9a-f]+[0-9a-f]*|0)$");
        bool hasChainId = json.TryGetProperty("chainId", out JsonElement chainId);
        if (hasChainId)
        {
            RpcTransactionAssertions.AssertMatchesWhenPresent(chainId.GetString(), "^0x([1-9a-f]+[0-9a-f]*|0)$");
        }
        RpcTransactionAssertions.AssertMatchesWhenPresent(json.GetProperty("v").GetString(), "^0x([1-9a-f]+[0-9a-f]*|0)$");
        RpcTransactionAssertions.AssertMatchesWhenPresent(json.GetProperty("r").GetString(), "^0x([1-9a-f]+[0-9a-f]*|0)$");
        RpcTransactionAssertions.AssertMatchesWhenPresent(json.GetProperty("s").GetString(), "^0x([1-9a-f]+[0-9a-f]*|0)$");
    }
}
