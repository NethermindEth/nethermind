// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;

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
        json.GetProperty("type").GetString().Should().MatchRegex("^0x0$");
        json.GetProperty("nonce").GetString().Should().MatchRegex("^0x([1-9a-f]+[0-9a-f]*|0)$");
        json.GetProperty("to").GetString()?.Should().MatchRegex("^0x[0-9a-fA-F]{40}$");
        json.GetProperty("from").GetString()?.Should().MatchRegex("^0x[0-9a-fA-F]{40}$");
        json.GetProperty("gas").GetString().Should().MatchRegex("^0x([1-9a-f]+[0-9a-f]*|0)$");
        json.GetProperty("value").GetString().Should().MatchRegex("^0x([1-9a-f]+[0-9a-f]*|0)$");
        json.GetProperty("input").GetString().Should().MatchRegex("^0x[0-9a-f]*$");
        json.GetProperty("gasPrice").GetString().Should().MatchRegex("^0x([1-9a-f]+[0-9a-f]*|0)$");
        bool hasChainId = json.TryGetProperty("chainId", out var chainId);
        if (hasChainId)
        {
            chainId.GetString().Should().MatchRegex("^0x([1-9a-f]+[0-9a-f]*|0)$");
        }
        json.GetProperty("v").GetString().Should().MatchRegex("^0x([1-9a-f]+[0-9a-f]*|0)$");
        json.GetProperty("r").GetString().Should().MatchRegex("^0x([1-9a-f]+[0-9a-f]*|0)$");
        json.GetProperty("s").GetString().Should().MatchRegex("^0x([1-9a-f]+[0-9a-f]*|0)$");
    }
}
