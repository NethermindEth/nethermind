// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Avalanche.Blocks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Avalanche.Test.Blocks;

/// <summary>
/// Validates the header codec against a real Avalanche C-Chain (Coreth) <b>mainnet</b> block, captured from the
/// public <c>api.avax.network</c> RPC. This is the byte-exact acceptance check: rebuilding the header from its
/// fields and hashing it must reproduce the block hash the network agreed on.
/// </summary>
/// <remarks>
/// Fixture: mainnet C-Chain block <c>89,117,142</c> (<c>0x54fd1d6</c>), hash
/// <c>0x238ba7b788d5fd5735a0bfa5977361fbcd3f116721c26dc847804ed357737d43</c>. This is a Granite-era block: it
/// carries <c>timestampMilliseconds</c> and <c>minDelayExcess</c>, and — because those trailing optionals are
/// set — the Avalanche-unused blob/beacon middle optionals are force-written as their zero encodings
/// (<c>blobGasUsed=0</c>, <c>excessBlobGas=0</c>, <c>parentBeaconBlockRoot</c> = 32 zero bytes), exercising the
/// full <c>rlp:"optional"</c> cascade. The block carries no atomic data, so <c>ExtDataHash</c> equals
/// <c>keccak256(0x80)</c>.
/// </remarks>
public class AvalancheRealBlockTests
{
    private const string ExpectedHash = "0x238ba7b788d5fd5735a0bfa5977361fbcd3f116721c26dc847804ed357737d43";

    private static AvalancheBlockHeader BuildMainnetBlock89117142() =>
        new(
            new Hash256("0x4032955e9de01d0b89c61d2a4eed6082e1014921ffb93b4f63964be38a2dea29"),
            new Hash256("0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347"),
            new Address("0x0100000000000000000000000000000000000000"),
            difficulty: (UInt256)1,
            number: 0x54fd1d6,
            gasLimit: 0x2625a00,
            timestamp: 0x6a4381ea,
            extraData: Bytes.FromHexString("0x00000000022ee75f0000000187e09e5e0000000002c5c860000000000000"))
        {
            StateRoot = new Hash256("0x6fe99dfe23a5dffc962ab76223013da6a7e4c5653d05b9f9c05d92981be5f080"),
            TxRoot = new Hash256("0x7bfef93be6fe23ccbbd4d4dfa7e2971171344acb21da156ade3e1d6c6d5115c5"),
            ReceiptsRoot = new Hash256("0xb086df5b9a4c326c742edab1a89cfd3a483fa46512158a2b8150989edf52fc4f"),
            Bloom = new Bloom(Bytes.FromHexString(
                "0x04000126800000000820100800010a012880000400100000c000116008000200044048000000000000101000102000800040010006000900041202000004008040c00080000500000000010800000400004008000004002001010082000210102000c000020100404000000400000a000000000000000020400002149000050000800000001200040001040500000008040040041000800000103000020000000004100800011008002009208002200052000000088000000000080000004400420080229000000000400040000008220010000000010004020008800008610404000000000000000040048800006000000020000080a0010040002204008100")),
            GasUsed = 0x3372a1,
            MixHash = new Hash256("0x0000000000000000000000000000000000000000000000000000000000000000"),
            Nonce = 0,
            ExtDataHash = new Hash256("0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421"),
            BaseFeePerGas = (UInt256)0x97669f0,
            ExtDataGasUsed = (UInt256)0,
            BlockGasCost = (UInt256)0,
            BlobGasUsed = 0,
            ExcessBlobGas = 0,
            ParentBeaconBlockRoot = new Hash256("0x0000000000000000000000000000000000000000000000000000000000000000"),
            TimeMilliseconds = 0x19f17b37d16,
            MinDelayExcess = 0x6e6965
        };

    [Test]
    public void ComputeHash_matches_real_mainnet_block_hash()
    {
        AvalancheBlockHeader header = BuildMainnetBlock89117142();

        Hash256 computed = AvalancheHeaderDecoder.Instance.ComputeHash(header);

        Assert.That(computed, Is.EqualTo(new Hash256(ExpectedHash)));
    }

    [Test]
    public void Header_encodes_to_24_item_list()
    {
        AvalancheBlockHeader header = BuildMainnetBlock89117142();
        byte[] encoded = AvalancheHeaderDecoder.Instance.Encode(header);

        Nethermind.Serialization.Rlp.RlpReader reader = new(encoded);
        int sequenceLength = reader.ReadSequenceLength();
        int itemCount = reader.PeekNumberOfItemsRemaining(reader.Position + sequenceLength);

        Assert.That(itemCount, Is.EqualTo(24));
    }

    [Test]
    public void Decoded_then_reencoded_round_trips_to_same_hash()
    {
        // Encode -> decode -> ComputeHash on the decoded header must still reproduce the network hash, proving
        // the positional decode + optional-cascade re-encode is byte-stable through a full round trip.
        AvalancheBlockHeader header = BuildMainnetBlock89117142();
        byte[] encoded = AvalancheHeaderDecoder.Instance.Encode(header);

        AvalancheBlockHeader decoded = AvalancheHeaderDecoder.Instance.Decode(encoded)!;

        Assert.That(decoded.Hash, Is.EqualTo(new Hash256(ExpectedHash)));
        Assert.That(AvalancheHeaderDecoder.Instance.ComputeHash(decoded), Is.EqualTo(new Hash256(ExpectedHash)));
    }
}
