// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Rpc;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public partial class EezSystemTransactionForRpcTests
{
    [OneTimeSetUp]
    public void RegisterNativeRpcType() => TransactionForRpc.RegisterTransactionType<EezNativeSystemTransactionForRpc>();

    [Test]
    public void Serialize_NativeTransaction_PreservesActualFieldsAndFixedPlaceholders([Values] bool history)
    {
        Transaction tx = new()
        {
            Type = EezConstants.NativeSystemTxType,
            ChainId = 901,
            Hash = TestItem.KeccakA,
            SourceHash = TestItem.KeccakB,
            SenderAddress = Address.SystemUser,
            To = history ? EezConstants.L1HistoryAddress : EezConstants.RollupXEezl2Address,
            GasLimit = history ? EezConstants.L1HistoryTxGasCap : 123_456UL,
            Value = history ? UInt256.Zero : (UInt256)7,
            Data = new byte[] { 1, 2 },
        };
        using JsonDocument json = JsonDocument.Parse(new EthereumJsonSerializer().Serialize(
            TransactionForRpc.FromTransaction(tx, new TransactionForRpcContext(901))));
        JsonElement root = json.RootElement;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(root.GetProperty("type").GetString(), Is.EqualTo("0x45"));
            Assert.That(root.GetProperty("version").GetString(), Is.EqualTo("0x1"));
            Assert.That(root.GetProperty("chainId").GetString(), Is.EqualTo("0x385"));
            Assert.That(root.GetProperty("hash").GetString(), Is.EqualTo(tx.Hash.ToString()));
            Assert.That(root.GetProperty("sourceHash").GetString(), Is.EqualTo(tx.SourceHash.ToString()));
            Assert.That(root.GetProperty("from").GetString(), Is.EqualTo(Address.SystemUser.ToString()));
            Assert.That(root.GetProperty("to").GetString(), Is.EqualTo(tx.To.ToString()));
            Assert.That(root.GetProperty("input").GetString(), Is.EqualTo("0x0102"));
            Assert.That(root.GetProperty("value").GetString(), Is.EqualTo(history ? "0x0" : "0x7"));
            Assert.That(root.GetProperty("gas").GetString(), Is.EqualTo(history ? "0x1e8480" : "0x1e240"));
            foreach (string field in new[] { "nonce", "gasPrice", "v", "r", "s", "yParity" })
                Assert.That(root.GetProperty(field).GetString(), Is.EqualTo("0x0"), field);
            foreach (string field in new[] { "accessList", "blobVersionedHashes", "authorizationList" })
                Assert.That(root.GetProperty(field).GetArrayLength(), Is.Zero, field);
        }
    }

    [Test]
    public void ToTransaction_NativeRpcType_RefusesSubmissionAndSimulation([Values] bool validateUserInput)
    {
        EezNativeSystemTransactionForRpc rpc = new()
        {
            ChainId = 901,
            SourceHash = TestItem.KeccakA,
            From = Address.SystemUser,
            To = EezConstants.RollupXEezl2Address,
            Value = 7,
            Gas = EezChainRules.RollupXSystemTxGasCap
        };
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rpc.ToTransaction(validateUserInput).IsError, Is.True);
            Assert.That(rpc.ShouldSetBaseFee(), Is.False);
        }
    }
}
