// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Eez.Execution;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Proofs;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public partial class EezSystemTxDecoderTests
{
    /// <summary>Canonical encoding of chain id 1, nonce 0, target EEZL2, value 0, input <c>0x01020304</c>.</summary>
    private static readonly byte[] CanonicalVector = Bytes.FromHexString("0x76dd0180944200000000000000000000000000000000000007808401020304");

    private const int CanonicalTargetByte = 10;

    [Test]
    public void Encode_CanonicalTransaction_ProducesTheCanonicalBytesAndHash()
    {
        Transaction tx = SystemTransactions.Create(chainId: 1, data: [1, 2, 3, 4]);

        byte[] encoded = TxDecoder.Instance.Encode(tx, RlpBehaviors.SkipTypedWrapping).Bytes;

        Assert.That(encoded, Is.EqualTo(CanonicalVector), "the wire bytes must match the canonical encoding byte for byte");
        Assert.That(tx.CalculateHash(), Is.EqualTo(Keccak.Compute(CanonicalVector)), "the hash covers the type byte and the body");
    }

    [Test]
    public void Decode_CanonicalBytes_GivesProtocolFieldsAndReencodesIdentically()
    {
        Transaction tx = Decode(CanonicalVector);

        Assert.That(tx.IsEezSystemTransaction(), Is.True, "the reserved type byte selects the system transaction codec");
        Assert.That(tx.HasSystemTransactionFields(EezChainRules.Rollup0), Is.True, "decoding supplies the fixed sender, target, budget and price");
        Assert.That(tx.Signature, Is.Null, "a system transaction is unsigned");
        Assert.That((tx.ChainId, tx.Nonce, tx.Value, tx.Data.ToArray()), Is.EqualTo((1UL, 0UL, UInt256.Zero, new byte[] { 1, 2, 3, 4 })),
            "the body fields are chain id, nonce, target, value and input in that order");
        Assert.That(TxDecoder.Instance.Encode(tx, RlpBehaviors.SkipTypedWrapping).Bytes, Is.EqualTo(CanonicalVector),
            "re-encoding a decoded transaction reproduces the wire bytes");
    }

    [Test]
    public void TxTrie_WithSystemTransaction_CommitsToTheWireBytes()
    {
        Transaction tx = Decode(CanonicalVector);

        Assert.That(TxTrie.CalculateRoot([tx]), Is.EqualTo(TxTrie.CalculateRoot([CanonicalVector])),
            "the transactions root must be computed over the wire bytes");
    }

    [Test]
    public void Decode_TruncatedCanonicalBytes_IsRejected()
    {
        for (int length = 1; length < CanonicalVector.Length; length++)
        {
            byte[] truncated = CanonicalVector[..length];
            Assert.That(() => Decode(truncated), Throws.InstanceOf<RlpException>(), $"a {length}-byte prefix is not a transaction");
        }
    }

    [Test]
    public void Decode_CanonicalBytesWithTrailingByte_IsRejected()
    {
        byte[] trailing = [.. CanonicalVector, 0x00];

        Assert.That(() => Decode(trailing), Throws.InstanceOf<RlpException>(), "bytes after the body make the encoding non-canonical");
    }

    [Test]
    public void Decode_CanonicalBytesWithFlippedTarget_IsRejected()
    {
        byte[] wrongTarget = (byte[])CanonicalVector.Clone();
        wrongTarget[CanonicalTargetByte] ^= 1;

        Assert.That(() => Decode(wrongTarget), Throws.InstanceOf<RlpException>().With.Message.Contains("must target"),
            "the reserved sender may only call the EEZL2 predeploy");
    }

    [Test]
    public void Decode_ContractCreation_IsRejected()
    {
        byte[] encoded = Bytes.Concat((byte)EezConstants.SystemTxType,
            Rlp.Encode(Rlp.Encode(1UL), Rlp.Encode(0UL), Rlp.Encode(Array.Empty<byte>()), Rlp.Encode(UInt256.Zero), Rlp.Encode(Array.Empty<byte>())).Bytes);

        Assert.That(() => Decode(encoded), Throws.InstanceOf<RlpException>(), "the wire format has no contract creation");
    }

    [Test]
    public void Decode_WithSignatureFields_IsRejected()
    {
        byte[] encoded = Bytes.Concat((byte)EezConstants.SystemTxType,
            Rlp.Encode(Rlp.Encode(1UL), Rlp.Encode(0UL), Rlp.Encode(EezConstants.Eezl2Address.Bytes), Rlp.Encode(UInt256.Zero),
                Rlp.Encode(Array.Empty<byte>()), Rlp.Encode(1UL), Rlp.Encode(TestItem.KeccakA.BytesToArray()), Rlp.Encode(TestItem.KeccakB.BytesToArray())).Bytes);

        Assert.That(() => Decode(encoded), Throws.InstanceOf<RlpException>().With.Message.Contains("trailing fields"),
            "the wire format has no signature");
    }

    private static IEnumerable<TestCaseData> NativeVectors()
    {
        JsonElement document = StatelessFixtures.ReadJson("rollupx/native-profile-v1", "vectors.json");
        foreach (JsonElement vector in document.GetProperty("vectors").EnumerateArray())
        {
            string id = vector.GetProperty("id").GetString()!;
            if (id is "native-history-repeat" or "native-history-advance")
                yield return new TestCaseData(Bytes.FromHexString(vector.GetProperty("expectedBytes").GetString()!),
                    new Hash256(vector.GetProperty("expectedKeccak256").GetString()!)).SetName($"NativeDecode_Upstream_{id}");
        }
    }

    [TestCaseSource(nameof(NativeVectors))]
    public void NativeDecode_IndependentVector_PreservesBytesAndHash(byte[] encoded, Hash256 expectedHash)
    {
        NativeDecoder decoder = new();
        Transaction tx = DecodeNative(encoded);
        byte[] actual = decoder.Encode(tx, RlpBehaviors.SkipTypedWrapping).Bytes;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual, Is.EqualTo(encoded));
            Assert.That(tx.Hash, Is.EqualTo(expectedHash));
            Assert.That((tx.Type, tx.SenderAddress, tx.Nonce, tx.Signature, tx.GasPrice),
                Is.EqualTo((EezConstants.NativeSystemTxType, Address.SystemUser, 0UL, (Signature?)null, UInt256.Zero)));
        }
    }

    [Test]
    public void NativeDecode_SevenFields_RoundTripsTheCanonicalCrossChainEnvelope()
    {
        byte[] encoded = Bytes.FromHexString("45f8430101a042424242424242424242424242424242424242424242424242424242424242428401000000944b2760f84b83d6b3829e30a7573b0172b6d6402c808401020304");
        Transaction tx = DecodeNative(encoded);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(new NativeDecoder().Encode(tx, RlpBehaviors.SkipTypedWrapping).Bytes, Is.EqualTo(encoded));
            Assert.That((tx.ChainId, tx.GasLimit, tx.To, tx.Value, tx.Data.ToArray()),
                Is.EqualTo((1UL, EezChainRules.RollupXSystemTxGasCap, EezConstants.RollupXEezl2Address, UInt256.Zero, new byte[] { 1, 2, 3, 4 })));
        }
    }

    private static IEnumerable<TestCaseData> MalformedNativeFields()
    {
        byte[] oversized = new byte[33];
        oversized.AsSpan().Fill(1);
        foreach ((int field, byte[] value, string name) in new (int, byte[], string)[]
        {
            (0, [], "MissingVersion"), (0, [2], "UnknownVersion"), (0, [0, 1], "PaddedVersion"),
            (1, [0, 1], "PaddedChainId"), (1, oversized[..9], "OversizedChainId"),
            (2, [], "MissingSource"), (2, new byte[31], "ShortSource"), (2, new byte[33], "LongSource"),
            (3, [0, 1], "PaddedGas"), (3, oversized[..9], "OversizedGas"), (3, [1, 0, 0, 1], "GasAboveCap"),
            (4, [], "Creation"), (4, new byte[19], "ShortTarget"), (4, new byte[21], "LongTarget"),
            (4, Address.Zero.Bytes.ToArray(), "ForeignTarget"), (5, [0], "NonCanonicalZeroValue"), (5, oversized, "OversizedValue"),
        })
            yield return new TestCaseData(field, value).SetName($"NativeDecode_{name}_Rejects");
    }

    [TestCaseSource(nameof(MalformedNativeFields))]
    public void NativeDecode_InvalidField_Rejects(int field, byte[] value)
    {
        byte[][] fields = NativeFields();
        fields[field] = value;
        Assert.That(() => DecodeNative(EncodeNativeFields(fields)), Throws.InstanceOf<RlpException>());
    }

    [Test]
    public void NativeDecode_MissingOrExtraField_Rejects([Values] bool extra)
    {
        byte[][] fields = NativeFields();
        byte[][] invalid = extra ? [.. fields, []] : fields[..6];
        Assert.That(() => DecodeNative(EncodeNativeFields(invalid)), Throws.InstanceOf<RlpException>());
    }

    [Test]
    public void NativeDecode_TruncatedOrTrailingBytes_Rejects([Values] bool allowExtraBytes)
    {
        RlpBehaviors behaviors = allowExtraBytes ? RlpBehaviors.AllowExtraBytes : RlpBehaviors.None;
        byte[] encoded = EncodeNativeFields(NativeFields());
        for (int length = 1; length < encoded.Length; length++)
        {
            byte[] prefix = encoded[..length];
            Assert.That(() => DecodeNative(prefix, behaviors), Throws.InstanceOf<RlpException>(), $"truncated native transaction length {length}");
        }
        Assert.That(() => DecodeNative([.. encoded, 0], behaviors), Throws.InstanceOf<RlpException>());
    }

    [Test]
    public void NativeDecode_InvalidHistoryImport_Rejects([Values] bool excessiveGas)
    {
        byte[][] fields = NativeFields();
        fields[3] = excessiveGas ? [0x1e, 0x84, 0x81] : [0x1e, 0x84, 0x80];
        fields[4] = EezConstants.L1HistoryAddress.Bytes.ToArray();
        fields[5] = excessiveGas ? [] : [1];
        Assert.That(() => DecodeNative(EncodeNativeFields(fields)), Throws.InstanceOf<RlpException>());
    }

    [Test]
    public void NativeEncode_SigningPayload_Rejects()
    {
        Transaction tx = DecodeNative(EncodeNativeFields(NativeFields()));
        Assert.That(() => new EezNativeSystemTxDecoder().GetLength(tx, RlpBehaviors.SkipTypedWrapping, forSigning: true),
            Throws.InstanceOf<RlpException>(), "the unsigned native envelope has no signing payload");
    }

    [Test]
    public void NativeDecode_NonzeroManagerValue_PreservesValue([Values((byte)0, (byte)42)] byte value)
    {
        byte[][] fields = NativeFields();
        fields[5] = value == 0 ? [] : [value];
        byte[] encoded = EncodeNativeFields(fields);
        Transaction tx = DecodeNative(encoded);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tx.Value, Is.EqualTo((UInt256)value));
            Assert.That(new NativeDecoder().Encode(tx, RlpBehaviors.SkipTypedWrapping).Bytes, Is.EqualTo(encoded));
        }
    }

    [Test]
    public void NativeDecode_ReusedTransaction_ResetsImplicitFields()
    {
        Transaction? tx = Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        tx.Nonce = 99;
        tx.GasPrice = 9;
        tx.DecodedMaxFeePerGas = 10;
        Assert.That(tx.Signature, Is.Not.Null, "precondition: the reused transaction has a signature");
        RlpReader reader = new(EncodeNativeFields(NativeFields()));
        new NativeDecoder().Decode(ref reader, ref tx, RlpBehaviors.SkipTypedWrapping);
        Assert.That((tx!.SenderAddress, tx.Nonce, tx.GasPrice, tx.DecodedMaxFeePerGas, tx.Signature),
            Is.EqualTo((Address.SystemUser, 0UL, UInt256.Zero, UInt256.Zero, (Signature?)null)));
    }

    [Test]
    public void NativeEncode_DirectSigningPayload_Rejects()
    {
        Transaction tx = DecodeNative(EncodeNativeFields(NativeFields()));
        Assert.That(() => EncodeNativeSigningPayload(tx), Throws.InstanceOf<RlpException>());
    }

    private static void EncodeNativeSigningPayload(Transaction tx)
    {
        RlpWriter writer = new(new byte[512]);
        new EezNativeSystemTxDecoder().Encode(tx, ref writer, RlpBehaviors.SkipTypedWrapping, forSigning: true);
    }

    private static byte[][] NativeFields()
    {
        byte[] source = new byte[32];
        source.AsSpan().Fill(0x42);
        return [[1], [1], source, [1, 0, 0, 0], EezConstants.RollupXEezl2Address.Bytes.ToArray(), [], [1, 2, 3, 4]];
    }

    private static byte[] EncodeNativeFields(byte[][] fields)
    {
        Rlp[] encoded = new Rlp[fields.Length];
        for (int i = 0; i < fields.Length; i++) encoded[i] = Rlp.Encode(fields[i]);
        return Bytes.Concat((byte)EezConstants.NativeSystemTxType, Rlp.Encode(encoded).Bytes);
    }

    private static Transaction DecodeNative(byte[] encoded, RlpBehaviors behaviors = RlpBehaviors.None)
    {
        RlpReader reader = new(encoded);
        Transaction? tx = null;
        new NativeDecoder().Decode(ref reader, ref tx, RlpBehaviors.SkipTypedWrapping | behaviors);
        return tx!;
    }

    private static Transaction Decode(byte[] encoded) => Rlp.Decode<Transaction>(encoded, RlpBehaviors.SkipTypedWrapping)!;

    private sealed class NativeDecoder : TxDecoder<Transaction>
    {
        internal NativeDecoder() => RegisterDecoder(new EezNativeSystemTxDecoder());
    }
}
