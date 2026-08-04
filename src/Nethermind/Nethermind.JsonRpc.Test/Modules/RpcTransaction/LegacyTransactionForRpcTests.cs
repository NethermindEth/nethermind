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

        BuildALegacyTransaction.WithNonce(0UL).TestObject,
        BuildALegacyTransaction.WithNonce(123UL).TestObject,
        BuildALegacyTransaction.WithNonce(ulong.MaxValue).TestObject,

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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(json.GetProperty("type").GetString(), Does.Match("^0x0$"));
            Assert.That(json.GetProperty("nonce").GetString(), Does.Match("^0x([1-9a-f]+[0-9a-f]*|0)$"));
            Assert.That(json.GetProperty("to").GetString(), Is.Null.Or.Matches("^0x[0-9a-fA-F]{40}$"));
            Assert.That(json.GetProperty("from").GetString(), Is.Null.Or.Matches("^0x[0-9a-fA-F]{40}$"));
            Assert.That(json.GetProperty("gas").GetString(), Does.Match("^0x([1-9a-f]+[0-9a-f]*|0)$"));
            Assert.That(json.GetProperty("value").GetString(), Does.Match("^0x([1-9a-f]+[0-9a-f]*|0)$"));
            Assert.That(json.GetProperty("input").GetString(), Does.Match("^0x[0-9a-f]*$"));
            Assert.That(json.GetProperty("gasPrice").GetString(), Does.Match("^0x([1-9a-f]+[0-9a-f]*|0)$"));
            bool hasChainId = json.TryGetProperty("chainId", out JsonElement chainId);
            if (hasChainId)
            {
                Assert.That(chainId.GetString(), Does.Match("^0x([1-9a-f]+[0-9a-f]*|0)$"));
            }
            Assert.That(json.GetProperty("v").GetString(), Does.Match("^0x([1-9a-f]+[0-9a-f]*|0)$"));
            Assert.That(json.GetProperty("r").GetString(), Does.Match("^0x([1-9a-f]+[0-9a-f]*|0)$"));
            Assert.That(json.GetProperty("s").GetString(), Does.Match("^0x([1-9a-f]+[0-9a-f]*|0)$"));
        }
    }
}
