// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.JsonRpc.Data;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.RpcTransaction;

[TestFixture]
public class FrameTransactionForRpcTests
{
    private static Transaction BuildMinimalFrameTx() => new()
    {
        Type = TxType.FrameTx,
        ChainId = 3151908,
        Nonce = 0,
        SenderAddress = TestItem.AddressA,
        GasLimit = 1_000_000,
        GasPrice = 1,
        DecodedMaxFeePerGas = 100,
        Frames =
        [
            new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, gasLimit: 100_000, UInt256.Zero, default),
        ],
        FrameSignatures = [],
    };

    [Test]
    public void FromTransaction_FrameTx_ReturnedAsFrameTransactionForRpc()
    {
        Transaction tx = BuildMinimalFrameTx();

        TransactionForRpc rpc = TransactionForRpc.FromTransaction(tx);

        Assert.That(rpc, Is.InstanceOf<FrameTransactionForRpc>());
        Assert.That(rpc.Type, Is.EqualTo(TxType.FrameTx));
    }

    [Test]
    public void FrameTransactionForRpc_SerializesType_As_0x06()
    {
        Transaction tx = BuildMinimalFrameTx();
        TransactionForRpc rpc = TransactionForRpc.FromTransaction(tx);

        string json = new EthereumJsonSerializer().Serialize(rpc);
        using JsonDocument doc = JsonDocument.Parse(json);

        Assert.That(doc.RootElement.GetProperty("type").GetString(), Is.EqualTo("0x6"));
    }

    [Test]
    public void FrameTransactionForRpc_SerializesFrames()
    {
        Transaction tx = new()
        {
            Type = TxType.FrameTx,
            ChainId = 3151908,
            Nonce = 0,
            SenderAddress = TestItem.AddressA,
            GasLimit = 1_000_000,
            GasPrice = 1,
            DecodedMaxFeePerGas = 100,
            Frames =
            [
                new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, TestItem.AddressB, 50_000, (UInt256)1, default),
            ],
            FrameSignatures = [],
        };
        TransactionForRpc rpc = TransactionForRpc.FromTransaction(tx);

        string json = new EthereumJsonSerializer().Serialize(rpc);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement frames = doc.RootElement.GetProperty("frames");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(frames.GetArrayLength(), Is.EqualTo(1));
            Assert.That(frames[0].GetProperty("mode").GetInt32(), Is.EqualTo(TxFrame.ModeVerify));
            Assert.That(frames[0].GetProperty("flags").GetInt32(), Is.EqualTo(TxFrame.ApproveExecutionAndPayment));
            Assert.That(frames[0].GetProperty("target").GetString(), Is.EqualTo(TestItem.AddressB.ToString()));
            Assert.That(frames[0].GetProperty("gasLimit").GetString(), Does.Match("^0x[0-9a-f]+$"));
        }
    }

    [Test]
    public void FrameTransactionForRpc_SerializesSignatures()
    {
        Transaction tx = new()
        {
            Type = TxType.FrameTx,
            ChainId = 3151908,
            Nonce = 0,
            SenderAddress = TestItem.AddressA,
            GasLimit = 1_000_000,
            GasPrice = 1,
            DecodedMaxFeePerGas = 100,
            Frames =
            [
                new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, gasLimit: 100_000, UInt256.Zero, default),
            ],
            FrameSignatures =
            [
                new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, signer: null, msg: default, new byte[65]),
            ],
        };
        TransactionForRpc rpc = TransactionForRpc.FromTransaction(tx);

        string json = new EthereumJsonSerializer().Serialize(rpc);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement signatures = doc.RootElement.GetProperty("signatures");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(signatures.GetArrayLength(), Is.EqualTo(1));
            Assert.That(signatures[0].GetProperty("scheme").GetInt32(), Is.EqualTo(TxFrameSignature.SchemeSecp256k1));
        }
    }

    [Test]
    public void FrameTransactionForRpc_ToTransaction_RoundTripsType()
    {
        Transaction original = BuildMinimalFrameTx();
        TransactionForRpc rpc = TransactionForRpc.FromTransaction(original);

        Transaction roundTripped = ((FrameTransactionForRpc)rpc).ToTransaction().Data!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(roundTripped.Type, Is.EqualTo(TxType.FrameTx));
            Assert.That(roundTripped.Frames, Is.Not.Null);
            Assert.That(roundTripped.FrameSignatures, Is.Not.Null);
        }
    }

    [Test]
    public void ReceiptForRpc_FrameTx_ExposesPayer()
    {
        TxReceipt receipt = new()
        {
            TxType = TxType.FrameTx,
            Payer = TestItem.AddressA,
            Sender = TestItem.AddressB,
            BlockHash = Keccak.Zero,
        };

        ReceiptForRpc receiptForRpc = new(Keccak.Zero, receipt, blockTimestamp: 0, new TxGasInfo(UInt256.One));

        Assert.That(receiptForRpc.Payer, Is.EqualTo(TestItem.AddressA));
    }

    [Test]
    public void ReceiptForRpc_FrameTx_ExposesFrameReceipts()
    {
        TxReceipt receipt = new()
        {
            TxType = TxType.FrameTx,
            Payer = TestItem.AddressA,
            Sender = TestItem.AddressB,
            BlockHash = Keccak.Zero,
            FrameReceipts =
            [
                new TxFrameReceipt(TxFrameReceipt.StatusSuccess, gasUsed: 21_000, logs: []),
            ],
        };

        ReceiptForRpc receiptForRpc = new(Keccak.Zero, receipt, blockTimestamp: 0, new TxGasInfo(UInt256.One));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(receiptForRpc.FrameReceipts, Has.Length.EqualTo(1));
            Assert.That(receiptForRpc.FrameReceipts![0].Status, Is.EqualTo(TxFrameReceipt.StatusSuccess));
        }
    }
}
