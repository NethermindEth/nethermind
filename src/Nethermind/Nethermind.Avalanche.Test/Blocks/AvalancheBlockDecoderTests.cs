// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Avalanche.Blocks;
using Nethermind.Avalanche.Parity;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Avalanche.Test.Blocks;

/// <summary>
/// Round-trip tests for the Coreth <c>extblock</c> codec: <c>[Header, Txs, Uncles, Version, ExtData]</c> and the
/// matching body <c>[Txs, Uncles, Version, ExtData]</c>.
/// </summary>
public class AvalancheBlockDecoderTests
{
    private static readonly Address AddressA = new("0x0000000000000000000000000000000000000aaa");
    private static readonly Address AddressB = new("0x0000000000000000000000000000000000000bbb");

    private static AvalancheBlockHeader BuildHeader() => new(
        Keccak.Compute("parent"),
        Keccak.OfAnEmptySequenceRlp,
        Address.Zero,
        (UInt256)1_000_000,
        number: 100,
        gasLimit: 8_000_000,
        timestamp: 1_700_000_000,
        extraData: [])
    {
        StateRoot = Keccak.Compute("state"),
        TxRoot = Keccak.Compute("txs"),
        ReceiptsRoot = Keccak.Compute("receipts"),
        Bloom = Bloom.Empty,
        GasUsed = 42_000,
        MixHash = Keccak.Compute("mix"),
        Nonce = 7,
        ExtDataHash = (Hash256)AvalancheExtData.EmptyExtDataHash,
        BaseFeePerGas = (UInt256)25_000_000_000,
        ExtDataGasUsed = (UInt256)0,
        BlockGasCost = (UInt256)10_000
    };

    private static Transaction BuildLegacyTx(ulong nonce, ulong gasLimit, UInt256 value, Address to) => new()
    {
        Type = TxType.Legacy,
        Nonce = nonce,
        GasPrice = (UInt256)20_000_000_000,
        GasLimit = gasLimit,
        To = to,
        Value = value,
        Data = Array.Empty<byte>(),
        // A deterministic non-zero legacy signature (v = 27) so the tx encodes/decodes round-trip.
        Signature = new Signature((UInt256)1, (UInt256)2, 27)
    };

    private static AvalancheBlock BuildBlock(byte[]? extData, uint version = 1, bool withTxs = true)
    {
        Transaction[] txs = withTxs
            ? [BuildLegacyTx(0, 21_000, (UInt256)1_000, AddressA), BuildLegacyTx(1, 50_000, (UInt256)0, AddressB)]
            : [];

        AvalancheBlockBody body = new(txs, uncles: [], version, extData);
        return new AvalancheBlock(BuildHeader(), body);
    }

    private static void AssertTxsEqual(Transaction[] expected, Transaction[] actual)
    {
        Assert.That(actual.Length, Is.EqualTo(expected.Length));
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.That(actual[i].Type, Is.EqualTo(expected[i].Type));
            Assert.That(actual[i].Nonce, Is.EqualTo(expected[i].Nonce));
            Assert.That(actual[i].GasPrice, Is.EqualTo(expected[i].GasPrice));
            Assert.That(actual[i].GasLimit, Is.EqualTo(expected[i].GasLimit));
            Assert.That(actual[i].To, Is.EqualTo(expected[i].To));
            Assert.That(actual[i].Value, Is.EqualTo(expected[i].Value));
        }
    }

    [TestCase(null, TestName = "nil extData")]
    [TestCase(new byte[0], TestName = "empty extData")]
    [TestCase(new byte[] { 0xde, 0xad, 0xbe, 0xef }, TestName = "non-empty extData")]
    public void Block_round_trips(byte[]? extData)
    {
        AvalancheBlock original = BuildBlock(extData, version: 3);

        byte[] encoded = AvalancheBlockDecoder.Instance.Encode(original);
        AvalancheBlock decoded = AvalancheBlockDecoder.Instance.Decode(encoded)!;

        Assert.That(decoded.Header.Hash, Is.EqualTo(original.Header.Hash ?? AvalancheHeaderDecoder.Instance.ComputeHash(original.Header)));
        Assert.That(decoded.Version, Is.EqualTo(3u));
        Assert.That(decoded.Uncles, Is.Empty);
        AssertTxsEqual(original.Transactions, decoded.Transactions);
        // nil and empty both decode to an empty slice (rlp:"nil" wire-equivalence).
        Assert.That(decoded.ExtData ?? [], Is.EqualTo(extData ?? []));
    }

    [Test]
    public void Block_round_trips_with_no_transactions()
    {
        AvalancheBlock original = BuildBlock(extData: [0x01], version: 0, withTxs: false);

        byte[] encoded = AvalancheBlockDecoder.Instance.Encode(original);
        AvalancheBlock decoded = AvalancheBlockDecoder.Instance.Decode(encoded)!;

        Assert.That(decoded.Transactions, Is.Empty);
        Assert.That(decoded.Version, Is.EqualTo(0u));
        Assert.That(decoded.ExtData, Is.EqualTo(new byte[] { 0x01 }));
    }

    [Test]
    public void Block_rlp_is_five_element_list()
    {
        byte[] encoded = AvalancheBlockDecoder.Instance.Encode(BuildBlock(extData: [0xab], version: 2));

        RlpReader reader = new(encoded);
        int sequenceLength = reader.ReadSequenceLength();
        int itemCount = reader.PeekNumberOfItemsRemaining(reader.Position + sequenceLength);

        // [ Header, [Txs...], [Uncles...], Version, ExtData ]
        Assert.That(itemCount, Is.EqualTo(5));
    }

    [Test]
    public void Body_round_trips()
    {
        Transaction[] txs = [BuildLegacyTx(0, 21_000, (UInt256)5, AddressA)];
        AvalancheBlockBody original = new(txs, uncles: [], version: 9, extData: [0xca, 0xfe]);

        byte[] encoded = AvalancheBlockDecoder.Instance.EncodeBody(original);
        AvalancheBlockBody decoded = AvalancheBlockDecoder.Instance.DecodeBody(encoded);

        Assert.That(decoded.Version, Is.EqualTo(9u));
        Assert.That(decoded.Uncles, Is.Empty);
        Assert.That(decoded.ExtData, Is.EqualTo(new byte[] { 0xca, 0xfe }));
        AssertTxsEqual(txs, decoded.Transactions);
    }

    [Test]
    public void Body_rlp_is_four_element_list()
    {
        AvalancheBlockBody body = new([], uncles: [], version: 1, extData: []);
        byte[] encoded = AvalancheBlockDecoder.Instance.EncodeBody(body);

        RlpReader reader = new(encoded);
        int sequenceLength = reader.ReadSequenceLength();
        int itemCount = reader.PeekNumberOfItemsRemaining(reader.Position + sequenceLength);

        // [ [Txs...], [Uncles...], Version, ExtData ]
        Assert.That(itemCount, Is.EqualTo(4));
    }

    [Test]
    public void GetLength_matches_encoded_length()
    {
        AvalancheBlock block = BuildBlock(extData: [0x11, 0x22], version: 4);
        byte[] encoded = AvalancheBlockDecoder.Instance.Encode(block);

        Assert.That(AvalancheBlockDecoder.Instance.GetLength(block), Is.EqualTo(encoded.Length));
    }

    [Test]
    public void Block_hash_is_keccak_of_header_rlp_only()
    {
        // The block hash is derived from the header alone, independent of body content.
        AvalancheBlock withData = BuildBlock(extData: [0xde, 0xad], version: 1);
        AvalancheBlock withoutData = BuildBlock(extData: null, version: 1);

        Hash256 h1 = AvalancheHeaderDecoder.Instance.ComputeHash(withData.Header);
        Hash256 h2 = AvalancheHeaderDecoder.Instance.ComputeHash(withoutData.Header);

        Assert.That(h1, Is.EqualTo(h2));
    }
}
