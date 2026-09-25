// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Eez.Rpc;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public class EezSystemTransactionForRpcTests
{
    [OneTimeSetUp]
    public void RegisterRpcType() => TransactionForRpc.RegisterTransactionType<EezSystemTransactionForRpc>();

    [Test]
    public void Serialize_SystemTransaction_HasTheFixedGasPriceAndSignaturePlaceholders()
    {
        Transaction tx = SystemTransactions.Create(chainId: 1, nonce: 3, value: 5, data: [1, 2]);
        tx.Hash = tx.CalculateHash();

        using JsonDocument json = JsonDocument.Parse(new EthereumJsonSerializer().Serialize(TransactionForRpc.FromTransaction(tx, new TransactionForRpcContext(1))));
        JsonElement root = json.RootElement;

        Assert.That(root.GetProperty("type").GetString(), Is.EqualTo("0x76"), "RPC consumers identify the system transaction type");
        Assert.That(root.GetProperty("hash").GetString(), Is.EqualTo(tx.Hash!.ToString()), "the hash is the keccak of the wire bytes");
        Assert.That(root.GetProperty("chainId").GetString(), Is.EqualTo("0x1"), "the chain id is a body field");
        Assert.That(root.GetProperty("nonce").GetString(), Is.EqualTo("0x3"), "the nonce is a body field");
        Assert.That(root.TryGetProperty("yParity", out _), Is.False, "an unsigned transaction has no y parity");
        Assert.That(root.GetProperty("gas").GetString(), Is.EqualTo("0x1e8480"), "the implicit 2M budget is shown as the gas limit");
        Assert.That(root.GetProperty("gasPrice").GetString(), Is.EqualTo("0x0"), "system transactions pay no gas price");
        Assert.That(root.GetProperty("from").GetString(), Is.EqualTo(EezConstants.SystemAddress.ToString()), "the fixed sender is exposed as from");
        foreach (string field in new[] { "v", "r", "s" })
        {
            Assert.That(root.GetProperty(field).GetString(), Is.EqualTo("0x0"), $"the missing signature is shown as a zero {field}");
        }
    }

    [Test]
    public void ToTransaction_SystemTransaction_IsRefused()
    {
        EezSystemTransactionForRpc rpcTx = new() { To = EezConstants.Eezl2Address, Value = UInt256.One };

        Assert.That(rpcTx.ToTransaction().IsError, Is.True,
            "eth_call, eth_estimateGas and eth_sendTransaction must not build a system transaction from user input");
    }
}
