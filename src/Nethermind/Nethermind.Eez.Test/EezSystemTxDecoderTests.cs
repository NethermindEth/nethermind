// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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

public class EezSystemTxDecoderTests
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
        Assert.That(tx.HasSystemTransactionFields(), Is.True, "decoding supplies the fixed sender, target, budget and price");
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

    private static Transaction Decode(byte[] encoded) => Rlp.Decode<Transaction>(encoded, RlpBehaviors.SkipTypedWrapping)!;
}
