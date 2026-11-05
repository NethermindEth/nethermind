// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Text.Json;
using System.Globalization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Extensions;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public partial class EezSystemTxDecoderTests
{
    private static readonly string[] ChangedHistoryFields = ["type", "chain", "source", "sender", "target", "value", "nonce", "gas", "data", "fee"];

    [Test]
    public void HistoryTransaction_DecodesAndReencodesTheExactOwnedEnvelope([Values] bool wrapped, [Values] bool explicitBlocks)
    {
        NativeHistoryEnvelope envelope = HistoryEnvelope(explicitBlocks);
        EezNativeHistoryTxDecoder decoder = new(explicitBlocks ? NativeSystemTxFormat.ContractV6 : NativeSystemTxFormat.ContractV4);
        Transaction? decoded = null;
        byte[] wire = envelope.Encode();
        RlpReader reader = new(wire);
        reader.Position = 1;
        decoder.Decode(ref decoded, 0, wire, ref reader);
        NativeHistoryTransaction history = (NativeHistoryTransaction)decoded!;
        byte[] output = new byte[decoder.GetLength(history, wrapped ? RlpBehaviors.None : RlpBehaviors.SkipTypedWrapping)];
        RlpWriter writer = new(output);
        decoder.Encode(history, ref writer, wrapped ? RlpBehaviors.None : RlpBehaviors.SkipTypedWrapping);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(history.Hash, Is.EqualTo(Keccak.Compute(wire)));
            Assert.That(history.Data.IsEmpty, Is.True, "wire input contains hashes, not derived execution calldata");
            Assert.That(history.SourceHash, Is.Null);
            Assert.That(history.HasWireFields, Is.True);
            Assert.That(reader.Position, Is.EqualTo(wire.Length));
            Assert.That(output, Is.EqualTo(wrapped ? Rlp.Encode(wire).Bytes : wire));
            Assert.That(() => decoder.GetLength(history, RlpBehaviors.None, forSigning: true), Throws.TypeOf<RlpException>());
        }
    }

    [Test]
    public void HistoryTransaction_ExecutionViewPreservesOriginalFieldsAndHash([Values] bool explicitBlocks)
    {
        NativeHistoryEnvelope envelope = HistoryEnvelope(explicitBlocks);
        NativeHistoryTransaction wire = new(envelope) { Hash = TestItem.KeccakC };
        NativeHistoryTransaction execution = wire.ForExecution(120);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(wire.Data.IsEmpty, Is.True);
            Assert.That(wire.HasWireFields, Is.True);
            Assert.That(execution.Hash, Is.EqualTo(wire.Hash));
            Assert.That(execution.Data.ToArray(), Is.EqualTo(explicitBlocks ? NativeHistoryCall.EncodeBlocks(envelope) : NativeHistoryCall.Encode(120, envelope.Hashes)));
            Assert.That(execution.IsExecutionView, Is.True);
            Assert.That(() => new EezNativeHistoryTxDecoder().GetLength(execution, RlpBehaviors.None), Throws.TypeOf<RlpException>(),
                "derived execution calldata must never replace the captured wire envelope");
        }
    }

    [Test]
    public void HistoryTransaction_ChangedImplicitFieldsCannotBeEncoded(
        [ValueSource(nameof(ChangedHistoryFields))] string field, [Values] bool explicitBlocks)
    {
        NativeHistoryTransaction transaction = new(HistoryEnvelope(explicitBlocks));
        switch (field)
        {
            case "type": transaction.Type = EezConstants.NativeSystemTxType; break;
            case "chain": transaction.ChainId = 2; break;
            case "source": transaction.SourceHash = TestItem.KeccakB; break;
            case "sender": transaction.SenderAddress = TestItem.AddressA; break;
            case "target": transaction.To = TestItem.AddressA; break;
            case "value": transaction.Value = 1; break;
            case "nonce": transaction.Nonce = 1; break;
            case "gas": transaction.GasLimit--; break;
            case "data": transaction.Data = new byte[] { 1 }; break;
            case "fee": transaction.GasPrice = 1; break;
        }
        EezNativeHistoryTxDecoder decoder = new(explicitBlocks ? NativeSystemTxFormat.ContractV6 : NativeSystemTxFormat.ContractV4);
        Assert.That(() => decoder.GetLength(transaction, RlpBehaviors.None), Throws.TypeOf<RlpException>());
    }

    [Test]
    public void HistoryTransaction_WrongProfile_RejectsBothWireAndModel([Values] bool explicitBlocks)
    {
        NativeHistoryEnvelope envelope = HistoryEnvelope(explicitBlocks);
        EezNativeHistoryTxDecoder decoder = new(explicitBlocks ? NativeSystemTxFormat.ContractV4 : NativeSystemTxFormat.ContractV6);
        NativeHistoryTransaction transaction = new(envelope);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => decoder.GetLength(transaction, RlpBehaviors.None), Throws.TypeOf<RlpException>());
            Assert.That(() => DecodeHistory(decoder, envelope.Encode()), Throws.TypeOf<RlpException>());
        }
    }

    [Test]
    public void HistoryTransaction_ExplicitBlocks_CalldataMatchesUnchangedUpstreamOracle()
    {
        JsonElement vectors = StatelessFixtures.ReadJson("rollupx/native-profile-v6", "vectors.json").GetProperty("vectors");
        JsonElement call = vectors.EnumerateArray().First(static v => v.GetProperty("id").GetString() == "history-advance-calldata");
        string[] pair = call.GetProperty("arguments")[0].GetString()![2..^2].Split(',');
        NativeHistoryEnvelope envelope = NativeHistoryEnvelope.CreateBlocks(901,
            [new(ulong.Parse(pair[0], CultureInfo.InvariantCulture), new ValueHash256(pair[1]))]);
        byte[] actual = NativeHistoryCall.EncodeBlocks(envelope);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual, Is.EqualTo(Bytes.FromHexString(call.GetProperty("expectedBytes").GetString()!)));
            Assert.That(ValueKeccak.Compute(actual), Is.EqualTo(new ValueHash256(call.GetProperty("expectedKeccak256").GetString()!)));
        }
    }

    private static NativeHistoryEnvelope HistoryEnvelope(bool explicitBlocks) => explicitBlocks
        ? NativeHistoryEnvelope.CreateBlocks(1, [new(99, TestItem.KeccakA.ValueHash256), new(107, TestItem.KeccakB.ValueHash256)])
        : NativeHistoryEnvelope.Create(1, [TestItem.KeccakA.ValueHash256, TestItem.KeccakB.ValueHash256]);

    private static Transaction DecodeHistory(EezNativeHistoryTxDecoder decoder, byte[] wire)
    {
        Transaction? transaction = null;
        RlpReader reader = new(wire) { Position = 1 };
        decoder.Decode(ref transaction, 0, wire, ref reader);
        return transaction!;
    }
}
