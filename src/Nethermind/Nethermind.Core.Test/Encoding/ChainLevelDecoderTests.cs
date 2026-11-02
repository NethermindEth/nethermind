// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding;

public class ChainLevelDecoderTests
{
    [Test]
    public void Can_do_roundtrip([Values] bool valueDecode)
    {
        BlockInfo blockInfo = new(TestItem.KeccakA, 1);
        blockInfo.WasProcessed = true;

        BlockInfo blockInfo2 = new(TestItem.KeccakB, 2);
        blockInfo2.WasProcessed = false;

        ChainLevelInfo chainLevelInfo = new(true, new[] { blockInfo, blockInfo2 });
        chainLevelInfo.HasBlockOnMainChain = true;

        Rlp rlp = Rlp.Encode(chainLevelInfo);

        ChainLevelInfo decoded = (valueDecode ? Rlp.Decode<ChainLevelInfo>(rlp.Bytes.AsSpan()) : Rlp.Decode<ChainLevelInfo>(rlp))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.HasBlockOnMainChain, Is.True, "has block on the main chain");
            Assert.That(decoded.BlockInfos[0].WasProcessed, Is.True, "0 processed");
            Assert.That(decoded.BlockInfos[1].WasProcessed, Is.False, "1 not processed");
            Assert.That(decoded.BlockInfos[0].BlockHash, Is.EqualTo(TestItem.KeccakA), "block hash");
            Assert.That(decoded.BlockInfos[0].TotalDifficulty, Is.EqualTo(UInt256.One), "difficulty");
        }
    }

    [Test]
    public void Encode_matches_independently_built_rlp([Values(0, 1, 2, 5)] int blockCount, [Values] bool hasBlockOnMainChain)
    {
        BlockInfo[] blockInfos = new BlockInfo[blockCount];
        Rlp[] encodedInfos = new Rlp[blockCount];
        for (int i = 0; i < blockCount; i++)
        {
            BlockMetadata metadata = i % 3 == 0 ? BlockMetadata.None : BlockMetadata.Finalized | BlockMetadata.BeaconMainChain;
            blockInfos[i] = new BlockInfo(TestItem.Keccaks[i], new UInt256((ulong)i * 1_000_000_007UL), metadata)
            {
                WasProcessed = i % 2 == 0
            };
            encodedInfos[i] = Rlp.Encode(blockInfos[i]);
        }

        ChainLevelInfo chainLevelInfo = new(hasBlockOnMainChain, blockInfos);
        byte[] expected = Rlp.Encode(Rlp.Encode(hasBlockOnMainChain ? 1 : 0), Rlp.Encode(encodedInfos)).Bytes;
        ChainLevelDecoder decoder = new();

        using ArrayPoolSpan<byte> pooled = decoder.EncodeToArrayPoolSpan(chainLevelInfo);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoder.GetLength(chainLevelInfo, RlpBehaviors.None), Is.EqualTo(expected.Length));
            Assert.That(Rlp.Encode(chainLevelInfo).Bytes, Is.EqualTo(expected));
            Assert.That(((ReadOnlySpan<byte>)pooled).ToArray(), Is.EqualTo(expected));
        }
    }

    [Test]
    public void Can_handle_nulls()
    {
        Rlp rlp = Rlp.Encode((ChainLevelInfo)null!);
        ChainLevelInfo? decoded = Rlp.Decode<ChainLevelInfo>(rlp);
        Assert.That(decoded, Is.Null);
    }
}
