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
/// Round-trip and structural tests for the Coreth header codec at each historical fork shape, plus the
/// <c>ExtDataHash</c>/block-hash invariants.
/// </summary>
/// <remarks>
/// Byte-exact validation against a real mainnet C-Chain block (fetch the block via <c>debug_getRawBlock</c> and
/// assert the decoded <c>Header.Hash</c> equals the node's reported block hash) is the final acceptance check;
/// it is pending the C-Chain RPC coming online and is intentionally not asserted here.
/// </remarks>
public class AvalancheHeaderDecoderTests
{
    private static readonly Hash256 ParentHash = Keccak.Compute("parent");
    private static readonly Hash256 StateRoot = Keccak.Compute("state");
    private static readonly Hash256 TxRoot = Keccak.Compute("txs");
    private static readonly Hash256 ReceiptsRoot = Keccak.Compute("receipts");
    private static readonly Hash256 MixHash = Keccak.Compute("mix");
    private static readonly Hash256 ExtDataHash = (Hash256)AvalancheExtData.EmptyExtDataHash;
    private static readonly Hash256 BeaconRoot = Keccak.Compute("beacon");

    /// <summary>Fork shapes and the resulting header RLP list length (number of top-level items).</summary>
    public enum Shape
    {
        /// <summary>Genesis / Apricot Phase 2: 15 Ethereum fields + ExtDataHash = 16 items.</summary>
        Genesis,

        /// <summary>Apricot Phase 3: + BaseFee = 17 items.</summary>
        ApricotPhase3,

        /// <summary>Apricot Phase 4: + ExtDataGasUsed + BlockGasCost = 19 items.</summary>
        ApricotPhase4,

        /// <summary>Cancun-era: + BlobGasUsed + ExcessBlobGas + ParentBeaconRoot = 22 items.</summary>
        Cancun
    }

    private static AvalancheBlockHeader BuildHeader(Shape shape)
    {
        AvalancheBlockHeader header = new(
            ParentHash,
            Keccak.OfAnEmptySequenceRlp,
            Address.Zero,
            (UInt256)1_000_000,
            number: 42,
            gasLimit: 8_000_000,
            timestamp: 1_700_000_000,
            extraData: [1, 2, 3, 4])
        {
            StateRoot = StateRoot,
            TxRoot = TxRoot,
            ReceiptsRoot = ReceiptsRoot,
            Bloom = Bloom.Empty,
            GasUsed = 21_000,
            MixHash = MixHash,
            Nonce = 0xABCDEF,
            ExtDataHash = ExtDataHash
        };

        if (shape >= Shape.ApricotPhase3)
        {
            header.BaseFeePerGas = (UInt256)25_000_000_000;
        }

        if (shape >= Shape.ApricotPhase4)
        {
            header.ExtDataGasUsed = (UInt256)1_234;
            header.BlockGasCost = (UInt256)10_000;
        }

        if (shape >= Shape.Cancun)
        {
            header.BlobGasUsed = 131_072;
            header.ExcessBlobGas = 262_144;
            header.ParentBeaconBlockRoot = BeaconRoot;
        }

        return header;
    }

    [TestCase(Shape.Genesis, 16)]
    [TestCase(Shape.ApricotPhase3, 17)]
    [TestCase(Shape.ApricotPhase4, 19)]
    [TestCase(Shape.Cancun, 22)]
    public void Header_rlp_list_length_matches_shape(Shape shape, int expectedItemCount)
    {
        AvalancheBlockHeader header = BuildHeader(shape);
        byte[] encoded = AvalancheHeaderDecoder.Instance.Encode(header);

        RlpReader reader = new(encoded);
        int sequenceLength = reader.ReadSequenceLength();
        int itemCount = reader.PeekNumberOfItemsRemaining(reader.Position + sequenceLength);

        Assert.That(itemCount, Is.EqualTo(expectedItemCount));
    }

    [TestCase(Shape.Genesis)]
    [TestCase(Shape.ApricotPhase3)]
    [TestCase(Shape.ApricotPhase4)]
    [TestCase(Shape.Cancun)]
    public void Header_round_trips(Shape shape)
    {
        AvalancheBlockHeader original = BuildHeader(shape);

        byte[] encoded = AvalancheHeaderDecoder.Instance.Encode(original);
        AvalancheBlockHeader decoded = AvalancheHeaderDecoder.Instance.Decode(encoded)!;

        Assert.That(decoded.ParentHash, Is.EqualTo(original.ParentHash));
        Assert.That(decoded.UnclesHash, Is.EqualTo(original.UnclesHash));
        Assert.That(decoded.Beneficiary, Is.EqualTo(original.Beneficiary));
        Assert.That(decoded.StateRoot, Is.EqualTo(original.StateRoot));
        Assert.That(decoded.TxRoot, Is.EqualTo(original.TxRoot));
        Assert.That(decoded.ReceiptsRoot, Is.EqualTo(original.ReceiptsRoot));
        Assert.That(decoded.Difficulty, Is.EqualTo(original.Difficulty));
        Assert.That(decoded.Number, Is.EqualTo(original.Number));
        Assert.That(decoded.GasLimit, Is.EqualTo(original.GasLimit));
        Assert.That(decoded.GasUsed, Is.EqualTo(original.GasUsed));
        Assert.That(decoded.Timestamp, Is.EqualTo(original.Timestamp));
        Assert.That(decoded.ExtraData, Is.EqualTo(original.ExtraData));
        Assert.That(decoded.MixHash, Is.EqualTo(original.MixHash));
        Assert.That(decoded.Nonce, Is.EqualTo(original.Nonce));
        Assert.That(decoded.ExtDataHash, Is.EqualTo(original.ExtDataHash));
        Assert.That(decoded.BaseFeePerGas, Is.EqualTo(original.BaseFeePerGas));
        Assert.That(decoded.ExtDataGasUsed, Is.EqualTo(original.ExtDataGasUsed));
        Assert.That(decoded.BlockGasCost, Is.EqualTo(original.BlockGasCost));
        Assert.That(decoded.BlobGasUsed, Is.EqualTo(original.BlobGasUsed));
        Assert.That(decoded.ExcessBlobGas, Is.EqualTo(original.ExcessBlobGas));
        Assert.That(decoded.ParentBeaconBlockRoot, Is.EqualTo(original.ParentBeaconBlockRoot));
    }

    [TestCase(Shape.Genesis)]
    [TestCase(Shape.ApricotPhase3)]
    [TestCase(Shape.ApricotPhase4)]
    [TestCase(Shape.Cancun)]
    public void Optional_tail_fields_default_to_null_when_absent(Shape shape)
    {
        AvalancheBlockHeader decoded = AvalancheHeaderDecoder.Instance.Decode(
            AvalancheHeaderDecoder.Instance.Encode(BuildHeader(shape)))!;

        // Anything not part of this shape must come back null/zero, confirming the trailing-optional contract.
        if (shape < Shape.ApricotPhase3) Assert.That(decoded.BaseFeePerGas.IsZero, Is.True);
        if (shape < Shape.ApricotPhase4)
        {
            Assert.That(decoded.ExtDataGasUsed, Is.Null);
            Assert.That(decoded.BlockGasCost, Is.Null);
        }
        if (shape < Shape.Cancun)
        {
            Assert.That(decoded.BlobGasUsed, Is.Null);
            Assert.That(decoded.ExcessBlobGas, Is.Null);
            Assert.That(decoded.ParentBeaconBlockRoot, Is.Null);
        }
    }

    [Test]
    public void Decode_sets_hash_to_keccak_of_header_rlp()
    {
        AvalancheBlockHeader header = BuildHeader(Shape.ApricotPhase4);
        byte[] encoded = AvalancheHeaderDecoder.Instance.Encode(header);

        AvalancheBlockHeader decoded = AvalancheHeaderDecoder.Instance.Decode(encoded)!;

        Assert.That(decoded.Hash, Is.EqualTo(Keccak.Compute(encoded)));
    }

    [Test]
    public void ComputeHash_equals_keccak_of_header_rlp()
    {
        AvalancheBlockHeader header = BuildHeader(Shape.Cancun);
        byte[] encoded = AvalancheHeaderDecoder.Instance.Encode(header);

        Hash256 hash = AvalancheHeaderDecoder.Instance.ComputeHash(header);

        Assert.That(hash, Is.EqualTo(Keccak.Compute(encoded)));
    }

    [Test]
    public void ExtDataHash_for_empty_extData_is_empty_constant()
    {
        // A header committing to empty atomic data carries EmptyExtDataHash = keccak256(0x80).
        Hash256 extDataHash = (Hash256)AvalancheExtData.CalcExtDataHash(ReadOnlySpan<byte>.Empty);

        Assert.That(extDataHash, Is.EqualTo((Hash256)AvalancheExtData.EmptyExtDataHash));
    }

    [Test]
    public void ExtDataHash_for_nonempty_extData_matches_calc()
    {
        byte[] extData = Bytes.FromHexString("deadbeefcafe");
        Hash256 extDataHash = (Hash256)AvalancheExtData.CalcExtDataHash(extData);

        AvalancheBlockHeader header = BuildHeader(Shape.ApricotPhase4);
        header.ExtDataHash = extDataHash;

        AvalancheBlockHeader decoded = AvalancheHeaderDecoder.Instance.Decode(
            AvalancheHeaderDecoder.Instance.Encode(header))!;

        Assert.That(decoded.ExtDataHash, Is.EqualTo(extDataHash));
        Assert.That(decoded.ExtDataHash, Is.Not.EqualTo((Hash256)AvalancheExtData.EmptyExtDataHash));
    }

    [Test]
    public void GetLength_matches_encoded_length()
    {
        foreach (Shape shape in Enum.GetValues<Shape>())
        {
            AvalancheBlockHeader header = BuildHeader(shape);
            byte[] encoded = AvalancheHeaderDecoder.Instance.Encode(header);

            Assert.That(AvalancheHeaderDecoder.Instance.GetLength(header), Is.EqualTo(encoded.Length), $"shape={shape}");
        }
    }
}
