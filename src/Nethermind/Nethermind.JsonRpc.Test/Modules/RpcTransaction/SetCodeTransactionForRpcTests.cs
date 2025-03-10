// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Text.Json;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Test.Modules.RpcTransaction;

public static class SetCodeTransactionForRpcTests
{
    private static TransactionBuilder<Transaction> Build => Core.Test.Builders.Build.A.Transaction.WithType(TxType.SetCode)
        // NOTE: We require to initialize the authorization list
        .WithAuthorizationCodeIfAuthorizationListTx();

    public static readonly Transaction[] Transactions =
    [
        Build.TestObject,

        Build.WithNonce(UInt256.Zero).TestObject,
        Build.WithNonce((UInt256)123).TestObject,
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
        Build.WithValue((UInt256)123).TestObject,
        Build.WithValue(UInt256.MaxValue).TestObject,

        Build.WithData(TestItem.RandomDataA).TestObject,
        Build.WithData(TestItem.RandomDataB).TestObject,
        Build.WithData(TestItem.RandomDataC).TestObject,
        Build.WithData(TestItem.RandomDataD).TestObject,

        Build.WithGasPrice(UInt256.Zero).TestObject,
        Build.WithGasPrice((UInt256)123).TestObject,
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

        Build.WithAuthorizationCode(new AuthorizationTuple(
            chainId: BlockchainIds.Mainnet,
            codeAddress: TestItem.AddressA,
            nonce: 123,
            sig: TestItem.RandomSignatureA
        )).TestObject,

        Build.WithAuthorizationCode(new AuthorizationTuple(
            chainId: BlockchainIds.Sepolia,
            codeAddress: TestItem.AddressA,
            nonce: ulong.MaxValue,
            sig: TestItem.RandomSignatureA
        )).WithAuthorizationCode(new AuthorizationTuple(
            chainId: BlockchainIds.Sepolia,
            codeAddress: TestItem.AddressB,
            nonce: 0,
            sig: TestItem.RandomSignatureB
        )).TestObject,

        Build.WithSignature(TestItem.RandomSignatureA).TestObject,
        Build.WithSignature(TestItem.RandomSignatureB).TestObject,
    ];

    public static void ValidateSchema(JsonElement json)
    {
        json.GetProperty("type").GetString().Should().MatchRegex("^0x4$");
        json.GetProperty("nonce").GetString().Should().MatchRegex("^0x([1-9a-f]+[0-9a-f]*|0)$");
        json.GetProperty("to").GetString()?.Should().MatchRegex("^0x[0-9a-fA-F]{40}$");
        json.GetProperty("gas").GetString().Should().MatchRegex("^0x([1-9a-f]+[0-9a-f]*|0)$");
        json.GetProperty("value").GetString().Should().MatchRegex("^0x([1-9a-f]+[0-9a-f]*|0)$");
        json.GetProperty("input").GetString().Should().MatchRegex("^0x[0-9a-f]*$");
        json.GetProperty("maxPriorityFeePerGas").GetString().Should().MatchRegex("^0x([1-9a-f]+[0-9a-f]*|0)$");
        json.GetProperty("maxFeePerGas").GetString().Should().MatchRegex("^0x([1-9a-f]+[0-9a-f]*|0)$");
        var accessList = json.GetProperty("accessList").EnumerateArray();
        if (accessList.Any())
        {
            accessList.Should().AllSatisfy(static item =>
            {
                item.GetProperty("address").GetString().Should().MatchRegex("^0x[0-9a-fA-F]{40}$");
                item.GetProperty("storageKeys").EnumerateArray().Should().AllSatisfy(static key =>
                    key.GetString().Should().MatchRegex("^0x[0-9a-f]{64}$")
                );
            });
        }
        json.GetProperty("chainId").GetString().Should().MatchRegex("^0x([1-9a-f]+[0-9a-f]*|0)$");
        json.GetProperty("yParity").GetString().Should().MatchRegex("^0x([1-9a-f]+[0-9a-f]*|0)$");
        json.GetProperty("r").GetString().Should().MatchRegex("^0x([1-9a-f]+[0-9a-f]*|0)$");
        json.GetProperty("s").GetString().Should().MatchRegex("^0x([1-9a-f]+[0-9a-f]*|0)$");
        // NOTE: Empty authorization lists are considered invalid
        json.GetProperty("authorizationList").EnumerateArray().Should().AllSatisfy(static tuple =>
        {
            tuple.GetProperty("chainId").GetString().Should().MatchRegex("^0x([1-9a-f]+[0-9a-f]*|0)$");
            tuple.GetProperty("nonce").GetString().Should().MatchRegex("^0x([1-9a-f]+[0-9a-f]*|0)$");
            tuple.GetProperty("address").GetString().Should().MatchRegex("^0x[0-9a-fA-F]{40}$");
            tuple.GetProperty("yParity").GetString().Should().MatchRegex("^0x([1-9a-f]+[0-9a-f]*|0)$");
            tuple.GetProperty("r").GetString().Should().MatchRegex("^0x([1-9a-f]+[0-9a-f]*|0)$");
            tuple.GetProperty("s").GetString().Should().MatchRegex("^0x([1-9a-f]+[0-9a-f]*|0)$");
        });
    }
}
