// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Eez.Execution;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public partial class EezSystemTxDecoderTests
{
    private static IEnumerable<TestCaseData> ContractV3EnvelopeVectors()
    {
        foreach (JsonElement vector in StatelessFixtures.ReadJson("rollupx/native-profile-v3", "vectors.json").GetProperty("vectors").EnumerateArray())
            if (vector.TryGetProperty("prefix", out JsonElement prefix) && prefix.GetString() == "0x45" &&
                vector.GetProperty("fields").GetArrayLength() == 6)
                yield return new TestCaseData(vector).SetName($"ContractV3Envelope_Upstream_{vector.GetProperty("id").GetString()}");
    }

    [TestCaseSource(nameof(ContractV3EnvelopeVectors))]
    public void ContractV3Envelope_UpstreamVector_MatchesBytesAndFields(JsonElement vector)
    {
        byte[] bytes = Bytes.FromHexString(vector.GetProperty("expectedBytes").GetString()!);
        Transaction tx = DecodeContractV3(bytes);
        JsonElement fields = vector.GetProperty("fields");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(new ContractV3Decoder().Encode(tx, RlpBehaviors.SkipTypedWrapping).Bytes, Is.EqualTo(bytes));
            Assert.That(tx.Hash, Is.EqualTo(new Hash256(vector.GetProperty("expectedKeccak256").GetString()!)));
            Assert.That(tx.Type, Is.EqualTo(EezConstants.NativeSystemTxType));
            Assert.That(tx.ChainId, Is.EqualTo(901));
            Assert.That(tx.SourceHash, Is.EqualTo(new Hash256(fields[1].GetString()!)));
            Assert.That(tx.GasLimit, Is.EqualTo(EezChainRules.RollupXSystemTxGasCap));
            Assert.That(tx.To, Is.EqualTo(EezConstants.RollupXEezl2Address));
            Assert.That(tx.Value, Is.EqualTo(new UInt256(Bytes.FromHexString(fields[4].GetString()!), isBigEndian: true)));
            Assert.That(tx.Data.ToArray(), Is.EqualTo(Bytes.FromHexString(fields[5].GetString()!)));
            Assert.That((tx.SenderAddress, tx.Nonce, tx.GasPrice, tx.DecodedMaxFeePerGas, tx.Signature),
                Is.EqualTo((Address.SystemUser, 0UL, UInt256.Zero, UInt256.Zero, (Signature?)null)));
        }
    }

    [Test]
    public void ContractV3Envelope_RejectsRecordedVersionField() =>
        Assert.That(() => DecodeContractV3(EncodeNativeFields(NativeFields())), Throws.InstanceOf<RlpException>());

    [Test]
    public void ContractV3Envelope_HistoryDestination_RejectsInDecodeAndEncode([Values((byte)0, (byte)1)] byte value)
    {
        byte[][] fields = NativeFields()[1..];
        fields[2] = [0x1e, 0x84, 0x80];
        fields[3] = EezConstants.L1HistoryAddress.Bytes.ToArray();
        fields[4] = value == 0 ? [] : [value];
        Transaction tx = DecodeNative(EncodeNativeFields(NativeFields()));
        tx.To = EezConstants.L1HistoryAddress;
        tx.GasLimit = EezConstants.L1HistoryTxGasCap;
        tx.Value = value;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => DecodeContractV3(EncodeNativeFields(fields)), Throws.InstanceOf<RlpException>());
            Assert.That(() => new ContractV3Decoder().Encode(tx, RlpBehaviors.SkipTypedWrapping), Throws.InstanceOf<RlpException>());
        }
    }

    [Test]
    public void ContractV3Envelope_InvalidFormat_Rejects([Values(-1, 0, 2, 4)] int format) =>
        Assert.That(() => new EezNativeSystemTxDecoder((NativeSystemTxFormat)format), Throws.TypeOf<ArgumentOutOfRangeException>());

    [Test]
    public void ContractV3Envelope_ChainIdUint64Boundaries_RoundTrip([Values(0UL, 1UL, ulong.MaxValue)] ulong chainId)
    {
        byte[][] fields = NativeFields()[1..];
        fields[0] = chainId switch
        {
            0 => [],
            1 => [1],
            _ => [0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff],
        };
        byte[] bytes = EncodeNativeFields(fields);
        Transaction tx = DecodeContractV3(bytes);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tx.ChainId, Is.EqualTo(chainId));
            Assert.That(new ContractV3Decoder().Encode(tx, RlpBehaviors.SkipTypedWrapping).Bytes, Is.EqualTo(bytes));
        }
    }

    [Test]
    public void ContractV3Envelope_TruncationAndTrailingData_Reject([Values] bool allowExtraBytes)
    {
        byte[] bytes = EncodeNativeFields(NativeFields()[1..]);
        RlpBehaviors behaviors = allowExtraBytes ? RlpBehaviors.AllowExtraBytes : RlpBehaviors.None;
        for (int length = 1; length < bytes.Length; length++)
        {
            byte[] truncated = bytes[..length];
            Assert.That(() => DecodeContractV3(truncated, behaviors), Throws.InstanceOf<RlpException>(), $"length {length}");
        }
        Assert.That(() => DecodeContractV3([.. bytes, 0], behaviors), Throws.InstanceOf<RlpException>());
    }

    private static IEnumerable<TestCaseData> MalformedContractV3Fields()
    {
        foreach (TestCaseData test in MalformedNativeFields())
        {
            int field = (int)test.Arguments[0]!;
            if (field == 0) continue;
            yield return new TestCaseData(field - 1, test.Arguments[1]).SetName($"ContractV3_{test.TestName}");
        }
    }

    [TestCaseSource(nameof(MalformedContractV3Fields))]
    public void ContractV3Envelope_InvalidField_Rejects(int field, byte[] value)
    {
        byte[][] fields = NativeFields()[1..];
        fields[field] = value;
        Assert.That(() => DecodeContractV3(EncodeNativeFields(fields)), Throws.InstanceOf<RlpException>());
    }

    [Test]
    public void ContractV3Envelope_FieldCount_IsExact([Values] bool extra)
    {
        byte[][] fields = NativeFields()[1..];
        Assert.That(() => DecodeContractV3(EncodeNativeFields(extra ? [.. fields, []] : fields[..5])), Throws.InstanceOf<RlpException>());
    }

    [Test]
    public void ContractV3Envelope_Unsigned_HasNoSigningPayload()
    {
        Transaction tx = DecodeContractV3(EncodeNativeFields(NativeFields()[1..]));
        Assert.That(() => new EezNativeSystemTxDecoder(NativeSystemTxFormat.ContractV3).GetLength(tx, RlpBehaviors.SkipTypedWrapping, forSigning: true),
            Throws.InstanceOf<RlpException>());
    }

    private static Transaction DecodeContractV3(byte[] bytes, RlpBehaviors behaviors = RlpBehaviors.None)
    {
        RlpReader reader = new(bytes);
        Transaction? tx = null;
        new ContractV3Decoder().Decode(ref reader, ref tx, RlpBehaviors.SkipTypedWrapping | behaviors);
        return tx!;
    }

    private sealed class ContractV3Decoder : TxDecoder<Transaction>
    {
        internal ContractV3Decoder() => RegisterDecoder(new EezNativeSystemTxDecoder(NativeSystemTxFormat.ContractV3));
    }
}
