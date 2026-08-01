// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding;

/// <summary>
/// Decodes a frame-transaction payload produced by another client's encoder, covering the EIP-8250
/// keyed nonce and EIP-8272 reference fields.
/// </summary>
/// <remarks>
/// A round-trip test only proves this decoder agrees with this encoder. The vector below was produced
/// by the ethrex tooling, so it also pins the field order and the signature-hash preimage against a
/// second implementation: a transaction the two clients decode differently is a consensus split, and
/// a signature hash they compute differently makes every cross-client transaction unspendable.
/// </remarks>
public class FrameTxCrossClientVectorTests
{
    private const string Raw =
        "06f8f583301824c201070394aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaecc901038083030d" +
        "408080e1028094bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb82c35082303983010203f85cf8" +
        "5a0194aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa80b8411111111111111111111111111111" +
        "11111111111111111111111111111111111111111111111111111111111111111111111111111111" +
        "1111111111111111111111010880c0f847f845a0000102030405060708090a0b0c0d0e0f10111213" +
        "1415161718191a1b1c1d1e1f82232fa0aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" +
        "aaaaaaaaaaaaaaaa";

    private const string ExpectedSigHash = "abf6cf6612f31e28109ddc498492f29e1b34c1be1f7890a5bc917a8d0b438892";

    [Test]
    public void Decode_KeyedAndReferencingVector_MatchesTheProducingClient()
    {
        Transaction tx = Rlp.Decode<Transaction>(Bytes.FromHexString(Raw), RlpBehaviors.SkipTypedWrapping);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tx.Type, Is.EqualTo(TxType.FrameTx));
            Assert.That(tx.ChainId, Is.EqualTo(3151908UL));
            Assert.That(tx.NonceKeys, Is.EqualTo(new UInt256[] { 1, 7 }));
            Assert.That(tx.Nonce, Is.EqualTo(3UL));
            Assert.That(tx.SenderAddress, Is.EqualTo(new Address("0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")));
            Assert.That(tx.Frames!.Length, Is.EqualTo(2));
            Assert.That(tx.Frames[0].Target, Is.Null, "an empty target resolves to the sender");
            Assert.That(tx.Frames[1].Value, Is.EqualTo((UInt256)12345));
            Assert.That(tx.FrameSignatures!.Length, Is.EqualTo(1));
            Assert.That(tx.GasPrice, Is.EqualTo(UInt256.One));
            Assert.That(tx.DecodedMaxFeePerGas, Is.EqualTo((UInt256)8));
            Assert.That(tx.RecentRootReferences!.Length, Is.EqualTo(1));
            Assert.That(tx.RecentRootReferences[0].Slot, Is.EqualTo(9007UL));
            Assert.That(FrameTxSigHash.ComputeValue(tx), Is.EqualTo(new ValueHash256(ExpectedSigHash)));
        }
    }

    [Test]
    public void Encode_KeyedAndReferencingVector_ReproducesTheProducingClientsBytes()
    {
        Transaction tx = Rlp.Decode<Transaction>(Bytes.FromHexString(Raw), RlpBehaviors.SkipTypedWrapping);

        Assert.That(Rlp.Encode(tx, RlpBehaviors.SkipTypedWrapping).Bytes.ToHexString(), Is.EqualTo(Raw));
    }
}
