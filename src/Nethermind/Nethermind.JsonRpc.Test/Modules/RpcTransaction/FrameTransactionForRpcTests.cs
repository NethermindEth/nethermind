// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.JsonRpc.Converters;
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
            Assert.That(frames[0].GetProperty("executionGasLimit").GetString(), Does.Match("^0x[0-9a-f]+$"));
            Assert.That(frames[0].GetProperty("stateGasLimit").GetString(), Does.Match("^0x[0-9a-f]+$"));
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
    public void FrameTransactionForRpc_BindsSenderNonceKeysAndBlobFieldsFromJson()
    {
        FrameTransactionForRpc rpc = new()
        {
            ChainId = 3151908,
            Nonce = 0,
            MaxFeePerGas = 100,
            MaxPriorityFeePerGas = UInt256.Zero,
            Sender = TestItem.AddressA,
            NonceKeys = [(UInt256)7, (UInt256)9],
            MaxFeePerBlobGas = 123,
            BlobVersionedHashes = [TestItem.KeccakA.BytesToArray()],
            Frames =
            [
                new FrameForRpc
                {
                    Mode = TxFrame.ModeVerify,
                    Flags = TxFrame.ApproveExecutionAndPayment,
                    GasLimit = 100_000,
                    Value = UInt256.Zero,
                },
            ],
            Signatures = [],
        };

        string json = new EthereumJsonSerializer().Serialize(rpc);
        FrameTransactionForRpc back = (FrameTransactionForRpc)new EthereumJsonSerializer().Deserialize<TransactionForRpc>(json);
        Transaction tx = back.ToTransaction().Data!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(back.Sender, Is.EqualTo(TestItem.AddressA));
            Assert.That(back.NonceKeys, Is.EqualTo(new[] { (UInt256)7, (UInt256)9 }));
            Assert.That(back.MaxFeePerBlobGas, Is.EqualTo((UInt256)123));
            Assert.That(back.BlobVersionedHashes![0], Is.EqualTo(TestItem.KeccakA.BytesToArray()));
            Assert.That(tx.NonceKeys, Is.EqualTo(new[] { (UInt256)7, (UInt256)9 }));
            Assert.That(tx.MaxFeePerBlobGas, Is.EqualTo((UInt256)123));
            Assert.That(tx.BlobVersionedHashes![0], Is.EqualTo(TestItem.KeccakA.BytesToArray()));
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
                new FrameForRpc { Mode = TxFrame.ModeVerify, Flags = TxFrame.ApproveExecutionAndPayment, ExecutionGasLimit = frameGasLimit },
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
                new FrameForRpc { Mode = TxFrame.ModeVerify, Flags = TxFrame.ApproveExecutionAndPayment, ExecutionGasLimit = 30_000 },
                new FrameForRpc { Mode = TxFrame.ModeSender, Target = TestItem.AddressC, ExecutionGasLimit = 40_000 },
            ],
        };

        Transaction tx = rpc.ToTransaction(validateUserInput: true, gasCap: GasCap).Data!;

        Assert.That(tx.GasLimit, Is.EqualTo(70_000));
    }

    // A frame whose own two limits overflow saturates rather than wrapping, matching FrameTxDecoder,
    // so the same frame list reports the same GasLimit however the transaction was built.
    [Test]
    public void FrameTransactionForRpc_ToTransaction_SaturatesAFrameWhoseOwnLimitsOverflow()
    {
        FrameTransactionForRpc rpc = new()
        {
            To = TestItem.AddressB,
            Frames =
            [
                new FrameForRpc { Mode = TxFrame.ModeVerify, ExecutionGasLimit = ulong.MaxValue, StateGasLimit = 1 },
            ],
        };

        Transaction tx = rpc.ToTransaction(validateUserInput: true).Data!;

        Assert.That(tx.GasLimit, Is.EqualTo(ulong.MaxValue));
    }

    [Test]
    public void FrameTransactionForRpc_ToTransaction_LeavesTheFrameGasUncappedWithoutAGasCap()
    {
        FrameTransactionForRpc rpc = new()
        {
            To = TestItem.AddressB,
            Frames = [new FrameForRpc { Mode = TxFrame.ModeVerify, ExecutionGasLimit = ulong.MaxValue }],
        };

        Assert.That(rpc.ToTransaction(validateUserInput: true).IsError, Is.False);
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

    private static readonly LogEntry FrameLog = new(TestItem.AddressC, [1, 2, 3], [TestItem.KeccakA]);

    private static TxReceipt BuildFrameTxReceipt() => new()
    {
        TxType = TxType.FrameTx,
        Payer = TestItem.AddressA,
        Sender = TestItem.AddressB,
        BlockHash = Keccak.Zero,
        Logs = [FrameLog],
        FrameReceipts =
        [
            new TxFrameReceipt(TxFrameReceipt.StatusSuccess, executionGasUsed: 21_000, stateGasUsed: 97_920, logs: [FrameLog]),
            new TxFrameReceipt(TxFrameReceipt.StatusFailure, executionGasUsed: 5_000, stateGasUsed: 0, logs: []),
        ],
    };

    private static ReceiptForRpc ToRpc(TxReceipt receipt) =>
        new(Keccak.Zero, receipt, blockTimestamp: 0, new TxGasInfo(UInt256.One));

    [Test]
    public void ReceiptForRpc_FrameTx_ExposesPayerAndFrameReceipts()
    {
        ReceiptForRpc receiptForRpc = ToRpc(BuildFrameTxReceipt());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(receiptForRpc.Payer, Is.EqualTo(TestItem.AddressA));
            Assert.That(receiptForRpc.FrameReceipts, Has.Length.EqualTo(2));
            Assert.That(receiptForRpc.FrameReceipts![0].Status, Is.EqualTo(TxFrameReceipt.StatusSuccess));
            Assert.That(receiptForRpc.FrameReceipts[0].ExecutionGasUsed, Is.EqualTo(21_000));
            Assert.That(receiptForRpc.FrameReceipts[0].StateGasUsed, Is.EqualTo(97_920));
        }
    }

    [TestCase(false, TestName = "A frame receipt survives the in-memory round trip")]
    [TestCase(true, TestName = "A frame receipt survives the JSON round trip")]
    public void ReceiptForRpc_FrameTx_RoundTripsPayerAndFrameReceipts(bool throughJson)
    {
        ReceiptForRpc receiptForRpc = ToRpc(BuildFrameTxReceipt());
        if (throughJson)
        {
            EthereumJsonSerializer serializer = new();
            receiptForRpc = serializer.Deserialize<ReceiptForRpc>(serializer.Serialize(receiptForRpc));
        }

        TxReceipt roundTripped = receiptForRpc.ToReceipt();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(roundTripped.Payer, Is.EqualTo(TestItem.AddressA));
            Assert.That(roundTripped.FrameReceipts, Has.Length.EqualTo(2));
            Assert.That(roundTripped.FrameReceipts![0].Status, Is.EqualTo(TxFrameReceipt.StatusSuccess));
            Assert.That(roundTripped.FrameReceipts[0].ExecutionGasUsed, Is.EqualTo(21_000UL));
            Assert.That(roundTripped.FrameReceipts[0].StateGasUsed, Is.EqualTo(97_920UL));
            Assert.That(roundTripped.FrameReceipts[0].Logs, Has.Length.EqualTo(1));
            Assert.That(roundTripped.FrameReceipts[0].Logs[0].Address, Is.EqualTo(TestItem.AddressC));
            Assert.That(roundTripped.FrameReceipts[0].Logs[0].Data, Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(roundTripped.FrameReceipts[0].Logs[0].Topics, Is.EqualTo(new[] { TestItem.KeccakA }));
            Assert.That(roundTripped.FrameReceipts[1].Status, Is.EqualTo(TxFrameReceipt.StatusFailure));
            Assert.That(roundTripped.FrameReceipts[1].ExecutionGasUsed, Is.EqualTo(5_000UL));
            Assert.That(roundTripped.FrameReceipts[1].StateGasUsed, Is.EqualTo(0UL));
        }
    }

    /// <summary>The wire-facing converter, not just the DTO, must carry the frame fields both ways.</summary>
    /// <remarks><see cref="TxReceiptConverter"/> is the registered converter for <see cref="TxReceipt"/>.</remarks>
    [Test]
    public void TxReceiptConverter_FrameTx_RoundTripsPayerAndFrameReceipts()
    {
        TxReceipt receipt = BuildFrameTxReceipt();
        receipt.TxHash = Keccak.Zero;
        EthereumJsonSerializer serializer = new(new JsonConverter[] { new TxReceiptConverter() });

        TxReceipt? roundTripped = serializer.Deserialize<TxReceipt>(serializer.Serialize(receipt));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(roundTripped!.Payer, Is.EqualTo(TestItem.AddressA));
            Assert.That(roundTripped.FrameReceipts, Has.Length.EqualTo(2));
            Assert.That(roundTripped.FrameReceipts![0].ExecutionGasUsed, Is.EqualTo(21_000UL));
            Assert.That(roundTripped.FrameReceipts[0].StateGasUsed, Is.EqualTo(97_920UL));
            Assert.That(roundTripped.FrameReceipts[0].Logs, Has.Length.EqualTo(1));
            Assert.That(roundTripped.FrameReceipts[1].Status, Is.EqualTo(TxFrameReceipt.StatusFailure));
            Assert.That(roundTripped.Logs, Has.Length.EqualTo(1));
        }
    }

    /// <summary>Frame receipts win over top-level fields that contradict them.</summary>
    /// <remarks>
    /// debug_insertReceipts binds arbitrary payloads, and <see cref="TxReceipt.FrameReceipts"/> requires
    /// <see cref="TxReceipt.Logs"/> to hold the frame log union, or the bloom disagrees with the frames.
    /// </remarks>
    [TestCase(true, TestName = "Frame receipts override contradicting top-level fields")]
    [TestCase(false, TestName = "Without frame receipts the top-level fields stand")]
    public void ReceiptForRpc_FrameTx_DerivesLogsAndStatusFromTheFrames(bool hasFrameReceipts)
    {
        ReceiptForRpc receiptForRpc = ToRpc(BuildFrameTxReceipt());
        receiptForRpc.Logs = [];
        receiptForRpc.Status = TxFrameReceipt.StatusSuccess;
        receiptForRpc.LogsBloom = new Bloom();
        if (!hasFrameReceipts)
        {
            receiptForRpc.FrameReceipts = null;
        }

        TxReceipt receipt = receiptForRpc.ToReceipt();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipt.Logs, Has.Length.EqualTo(hasFrameReceipts ? 1 : 0));
            Assert.That(receipt.StatusCode,
                Is.EqualTo(hasFrameReceipts ? TxFrameReceipt.StatusFailure : TxFrameReceipt.StatusSuccess));
            // Bloom and Logs are a matched pair: deriving one from the frames and keeping the caller's
            // other half would only move the contradiction. The wire receipt carries no bloom at all.
            Assert.That(receipt.Bloom, Is.EqualTo(hasFrameReceipts ? new Bloom([FrameLog]) : new Bloom()));
        }
    }

    private static IEnumerable<TestCaseData> MalformedFrameReceiptsCases()
    {
        yield return new TestCaseData("[null]").SetName("A null frame receipt entry is rejected");
        yield return new TestCaseData(
                $"[{string.Join(',', Enumerable.Repeat("{}", Eip8141Constants.MaxFrames + 1))}]")
            .SetName("More frame receipts than MAX_FRAMES are rejected");
    }

    /// <summary>A shape EIP-8141 cannot produce is a caller error, not a node error.</summary>
    /// <remarks>
    /// debug_insertReceipts binds these entries straight from JSON, and JsonRpcService answers a
    /// <see cref="JsonException"/> with invalid params where an unhandled one becomes an internal error.
    /// </remarks>
    [TestCaseSource(nameof(MalformedFrameReceiptsCases))]
    public void ReceiptForRpc_FrameTx_RejectsMalformedFrameReceipts(string frameReceiptsJson)
    {
        EthereumJsonSerializer serializer = new();
        ReceiptForRpc receiptForRpc = ToRpc(BuildFrameTxReceipt());
        receiptForRpc.FrameReceipts = serializer.Deserialize<FrameReceiptForRpc[]>(frameReceiptsJson);

        Assert.That(() => receiptForRpc.ToReceipt(), Throws.InstanceOf<JsonException>());
    }

    /// <summary>The same hazard on the top-level logs, which every receipt type carries.</summary>
    [Test]
    public void ReceiptForRpc_RejectsANullLogEntry()
    {
        ReceiptForRpc receiptForRpc = new EthereumJsonSerializer().Deserialize<ReceiptForRpc>("""{"logs":[null]}""");

        Assert.That(() => receiptForRpc.ToReceipt(), Throws.InstanceOf<JsonException>());
    }
}
