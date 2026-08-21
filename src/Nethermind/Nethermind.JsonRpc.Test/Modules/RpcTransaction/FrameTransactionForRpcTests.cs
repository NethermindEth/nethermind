// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
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
using static Nethermind.Core.Test.Builders.FrameTxTestFrames;

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
            SelfVerify(PrefixFrameGas),
        ],
        FrameSignatures = [],
    };

    private static readonly EthereumJsonSerializer Serializer = new();

    private static JsonDocument SerializeToJson(TransactionForRpc rpc) => JsonDocument.Parse(Serializer.Serialize(rpc));

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

        using JsonDocument doc = SerializeToJson(rpc);

        Assert.That(doc.RootElement.GetProperty("type").GetString(), Is.EqualTo("0x6"));
    }

    [Test]
    public void FrameTransactionForRpc_SerializesFrames()
    {
        Transaction tx = BuildMinimalFrameTx();
        tx.Frames = [new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, TestItem.AddressB, 50_000, (UInt256)1, default)];
        TransactionForRpc rpc = TransactionForRpc.FromTransaction(tx);

        using JsonDocument doc = SerializeToJson(rpc);
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
        Transaction tx = BuildMinimalFrameTx();
        tx.FrameSignatures = [new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, signer: null, msg: default, new byte[65])];
        TransactionForRpc rpc = TransactionForRpc.FromTransaction(tx);

        using JsonDocument doc = SerializeToJson(rpc);
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
    public void FrameTransactionForRpc_SerializesBlobFields_ForBlobCarryingFrameTx()
    {
        Transaction tx = BuildMinimalFrameTx();
        tx.MaxFeePerBlobGas = 123;
        tx.BlobVersionedHashes = [new byte[32]];

        TransactionForRpc rpc = TransactionForRpc.FromTransaction(tx);

        using JsonDocument doc = SerializeToJson(rpc);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(doc.RootElement.GetProperty("maxFeePerBlobGas").GetString(), Is.EqualTo("0x7b"));
            Assert.That(doc.RootElement.GetProperty("blobVersionedHashes").GetArrayLength(), Is.EqualTo(1));
        }
    }

    [Test]
    public void FrameTransactionForRpc_ReportsBlobFields_ForBloblessFrameTx()
    {
        Transaction tx = BuildMinimalFrameTx();
        TransactionForRpc rpc = TransactionForRpc.FromTransaction(tx);

        using JsonDocument doc = SerializeToJson(rpc);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(doc.RootElement.GetProperty("maxFeePerBlobGas").GetString(), Is.EqualTo("0x0"));
            Assert.That(doc.RootElement.GetProperty("blobVersionedHashes").GetArrayLength(), Is.EqualTo(0));
        }
    }

    // Must be 0 for a blobless tx, but the value still has to reach TXPARAM 0x05 in a simulation.
    [Test]
    public void FrameTransactionForRpc_ToTransaction_KeepsMaxFeePerBlobGas_WithoutBlobHashes()
    {
        FrameTransactionForRpc rpc = new() { MaxFeePerBlobGas = 123 };

        Transaction tx = rpc.ToTransaction().Data!;

        Assert.That(tx.MaxFeePerBlobGas, Is.EqualTo((UInt256)123));
    }

    [Test]
    public void FrameTransactionForRpc_ToTransaction_RoundTripsBlobFields()
    {
        Transaction original = BuildMinimalFrameTx();
        original.MaxFeePerBlobGas = 456;
        original.BlobVersionedHashes = [new byte[32]];
        TransactionForRpc rpc = TransactionForRpc.FromTransaction(original);

        Transaction roundTripped = ((FrameTransactionForRpc)rpc).ToTransaction().Data!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(roundTripped.MaxFeePerBlobGas, Is.EqualTo((UInt256)456));
            Assert.That(roundTripped.BlobVersionedHashes, Has.Length.EqualTo(1));
        }
    }

    private static Transaction BuildKeyedFrameTx(UInt256[]? nonceKeys, ulong nonceSeq = 3)
    {
        Transaction tx = BuildMinimalFrameTx();
        tx.NonceKeys = nonceKeys;
        tx.Nonce = nonceSeq;
        return tx;
    }

    private static IEnumerable<TestCaseData> NonceKeySets()
    {
        yield return new TestCaseData(new UInt256[] { 1, 7 }, new[] { "0x1", "0x7" }).SetArgDisplayNames("MultiKey");
        // [0] is the account-nonce domain, a different payload from the absent list.
        yield return new TestCaseData(new UInt256[] { 0 }, new[] { "0x0" }).SetArgDisplayNames("LegacyDomainSingleton");
    }

    [TestCaseSource(nameof(NonceKeySets))]
    public void FrameTransactionForRpc_SerializesNonceKeys(UInt256[] nonceKeys, string[] expected)
    {
        Transaction tx = BuildKeyedFrameTx(nonceKeys);
        TransactionForRpc rpc = TransactionForRpc.FromTransaction(tx);

        using JsonDocument doc = SerializeToJson(rpc);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(doc.RootElement.TryGetProperty("nonceKeys", out JsonElement keys), Is.True);
            Assert.That(keys.EnumerateArray().Select(static k => k.GetString()), Is.EqualTo(expected));
            Assert.That(doc.RootElement.GetProperty("nonce").GetString(), Is.EqualTo("0x3"));
        }
    }

    [Test]
    public void FrameTransactionForRpc_OmitsNonceKeys_ForEnvelopeNonce()
    {
        Transaction tx = BuildKeyedFrameTx(nonceKeys: null);
        TransactionForRpc rpc = TransactionForRpc.FromTransaction(tx);

        using JsonDocument doc = SerializeToJson(rpc);

        Assert.That(doc.RootElement.TryGetProperty("nonceKeys", out _), Is.False);
    }

    [TestCaseSource(nameof(NonceKeySets))]
    public void FrameTransactionForRpc_ToTransaction_RoundTripsNonceKeys(UInt256[] nonceKeys, string[] _)
    {
        Transaction original = BuildKeyedFrameTx(nonceKeys);
        TransactionForRpc rpc = TransactionForRpc.FromTransaction(original);

        Transaction roundTripped = ((FrameTransactionForRpc)rpc).ToTransaction().Data!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(roundTripped.NonceKeys, Is.EqualTo(nonceKeys));
            Assert.That(roundTripped.Nonce, Is.EqualTo(original.Nonce));
        }
    }

    [Test]
    public void FrameTransactionForRpc_ToTransaction_KeepsAbsentNonceKeys()
    {
        Transaction original = BuildKeyedFrameTx(nonceKeys: null);
        TransactionForRpc rpc = TransactionForRpc.FromTransaction(original);

        Transaction roundTripped = ((FrameTransactionForRpc)rpc).ToTransaction().Data!;

        Assert.That(roundTripped.NonceKeys, Is.Null);
    }

    [Test]
    public void FrameTransactionForRpc_DeserializesNonceKeys_FromCallParams()
    {
        const string json = """
            {
                "from": "0x0000000000000000000000000000000000000001",
                "nonce": "0x3",
                "nonceKeys": ["0x1", "0x7"],
                "frames": [{"mode": 0, "flags": 3, "gasLimit": "0x186a0", "value": "0x0", "data": "0x"}]
            }
            """;

        TransactionForRpc rpc = Serializer.Deserialize<TransactionForRpc>(json);

        Assert.That(rpc, Is.InstanceOf<FrameTransactionForRpc>());
        Transaction tx = rpc.ToTransaction().Data!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tx.NonceKeys, Is.EqualTo(new UInt256[] { 1, 7 }));
            Assert.That(tx.Nonce, Is.EqualTo(3UL));
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
