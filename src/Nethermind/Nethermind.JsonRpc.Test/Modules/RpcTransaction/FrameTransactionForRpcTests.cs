// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
    public void FrameTransactionForRpc_SerializesBlobFields_ForBlobCarryingFrameTx()
    {
        Transaction tx = BuildMinimalFrameTx();
        tx.MaxFeePerBlobGas = 123;
        tx.BlobVersionedHashes = [new byte[32]];

        TransactionForRpc rpc = TransactionForRpc.FromTransaction(tx);

        string json = new EthereumJsonSerializer().Serialize(rpc);
        using JsonDocument doc = JsonDocument.Parse(json);

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

        string json = new EthereumJsonSerializer().Serialize(rpc);
        using JsonDocument doc = JsonDocument.Parse(json);

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

    /// <remarks>
    /// A JSON <c>null</c> in any of the EIP-8141 lists — as an element, or as one of a reference's hashes, which
    /// System.Text.Json assigns past the converter rather than rejecting — deserializes to a null the mapping used
    /// to dereference, so <c>eth_call</c> answered these requests with a <see cref="NullReferenceException"/>.
    /// </remarks>
    [TestCase("""{"type":"0x6","to":"0x0000000000000000000000000000000000000002","frames":[null]}""", "frames", TestName = "ToTransaction_NullFrame_IsRejected")]
    [TestCase("""{"type":"0x6","to":"0x0000000000000000000000000000000000000002","signatures":[null]}""", "signatures", TestName = "ToTransaction_NullSignature_IsRejected")]
    [TestCase("""{"type":"0x6","to":"0x0000000000000000000000000000000000000002","recentRootReferences":[null]}""", "recentRootReferences", TestName = "ToTransaction_NullRecentRootReference_IsRejected")]
    [TestCase("""{"type":"0x6","to":"0x0000000000000000000000000000000000000002","recentRootReferences":[{"sourceId":null,"slot":"0x1","root":"0x0000000000000000000000000000000000000000000000000000000000000001"}]}""", "recentRootReferences", TestName = "ToTransaction_NullRecentRootReferenceSourceId_IsRejected")]
    [TestCase("""{"type":"0x6","to":"0x0000000000000000000000000000000000000002","recentRootReferences":[{"sourceId":"0x0000000000000000000000000000000000000000000000000000000000000001","slot":"0x1","root":null}]}""", "recentRootReferences", TestName = "ToTransaction_NullRecentRootReferenceRoot_IsRejected")]
    public void FrameTransactionForRpc_ToTransaction_RejectsANullListEntry(string json, string field)
    {
        TransactionForRpc rpc = new EthereumJsonSerializer().Deserialize<TransactionForRpc>(json);

        Result<Transaction> result = rpc.ToTransaction(validateUserInput: true, gasCap: GasCap);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsError, Is.True);
            Assert.That(result.Error, Is.EqualTo(RpcTransactionErrors.NullEntryIn(field)));
        }
    }

    private const ulong GasCap = 100_000;

    /// <summary>The RPC gas cap bounds a frame transaction by its frame gas limits.</summary>
    /// <remarks>
    /// The base mapping caps <see cref="Transaction.GasLimit"/>, which carries the request's <c>gas</c> field;
    /// the frame path never reads it and prices the transaction from the frames instead, so before this an
    /// <c>eth_call</c> could ask a node for arbitrarily more gas than its cap by putting it in a frame.
    /// </remarks>
    [TestCase(GasCap, false, TestName = "ToTransaction_FrameGasAtTheCap_IsAccepted")]
    [TestCase(GasCap + 1, true, TestName = "ToTransaction_FrameGasAboveTheCap_IsRejected")]
    [TestCase(ulong.MaxValue, true, TestName = "ToTransaction_FrameGasOverflowingTheSum_IsRejected")]
    public void FrameTransactionForRpc_ToTransaction_CapsTheFrameGasLimits(ulong frameGasLimit, bool expectedError)
    {
        FrameTransactionForRpc rpc = new()
        {
            To = TestItem.AddressB,
            Frames =
            [
                new FrameForRpc { Mode = TxFrame.ModeVerify, Flags = TxFrame.ApproveExecutionAndPayment, GasLimit = frameGasLimit },
                new FrameForRpc { Mode = TxFrame.ModeSender, Target = TestItem.AddressC },
            ],
        };

        Result<Transaction> result = rpc.ToTransaction(validateUserInput: true, gasCap: GasCap);

        Assert.That(result.IsError, Is.EqualTo(expectedError), result.Error);
    }

    /// <remarks>
    /// <c>FrameTxDecoder</c> gives a decoded frame tx the sum of its frame gas limits, since the type has no
    /// <c>gas_limit</c> field of its own. An RPC-built one has to agree, or the consumers that read
    /// <see cref="Transaction.GasLimit"/> before execution see a different transaction depending on its origin.
    /// </remarks>
    [Test]
    public void FrameTransactionForRpc_ToTransaction_ReportsTheFrameGasSumAsTheGasLimit()
    {
        FrameTransactionForRpc rpc = new()
        {
            To = TestItem.AddressB,
            Gas = 12,
            Frames =
            [
                new FrameForRpc { Mode = TxFrame.ModeVerify, Flags = TxFrame.ApproveExecutionAndPayment, GasLimit = 30_000 },
                new FrameForRpc { Mode = TxFrame.ModeSender, Target = TestItem.AddressC, GasLimit = 40_000 },
            ],
        };

        Transaction tx = rpc.ToTransaction(validateUserInput: true, gasCap: GasCap).Data!;

        Assert.That(tx.GasLimit, Is.EqualTo(70_000));
    }

    [Test]
    public void FrameTransactionForRpc_ToTransaction_LeavesTheFrameGasUncappedWithoutAGasCap()
    {
        FrameTransactionForRpc rpc = new()
        {
            To = TestItem.AddressB,
            Frames = [new FrameForRpc { Mode = TxFrame.ModeVerify, GasLimit = ulong.MaxValue }],
        };

        Assert.That(rpc.ToTransaction(validateUserInput: true).IsError, Is.False);
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
