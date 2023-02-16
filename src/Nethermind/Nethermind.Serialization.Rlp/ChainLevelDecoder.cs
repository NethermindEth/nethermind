// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Serialization.Rlp
{
    public class ChainLevelDecoder : IRlpStreamDecoder<ChainLevelInfo>, IRlpValueDecoder<ChainLevelInfo>
    {
        public ChainLevelInfo? Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (rlpStream.Length == 0)
            {
                throw new RlpException($"Received a 0 length stream when decoding a {nameof(ChainLevelInfo)}");
            }

            if (rlpStream.IsNextItemNull())
            {
                return null;
            }

            int lastCheck = rlpStream.ReadSequenceLength() + rlpStream.Position;
            bool hasMainChainBlock = rlpStream.DecodeBool();

            List<BlockInfo> blockInfos = new();

            rlpStream.ReadSequenceLength();
            while (rlpStream.Position < lastCheck)
            {
                // block info can be null for corrupted states (also cases where block hash is null from the old DBs)
                BlockInfo? blockInfo = Rlp.Decode<BlockInfo?>(rlpStream, RlpBehaviors.AllowExtraBytes);
                if (blockInfo is not null)
                {
                    blockInfos.Add(blockInfo);
                }
            }

            if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
            {
                rlpStream.Check(lastCheck);
            }

            ChainLevelInfo info = new(hasMainChainBlock, blockInfos.ToArray());
            return info;
        }

        public void Encode(RlpStream stream, ChainLevelInfo item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            throw new NotImplementedException();
        }

        public ChainLevelInfo? Decode(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (decoderContext.IsNextItemNull())
            {
                return null;
            }

            int lastCheck = decoderContext.ReadSequenceLength() + decoderContext.Position;
            bool hasMainChainBlock = decoderContext.DecodeBool();

            List<BlockInfo> blockInfos = new();

            decoderContext.ReadSequenceLength();
            while (decoderContext.Position < lastCheck)
            {
                // block info can be null for corrupted states (also cases where block hash is null from the old DBs)
                BlockInfo? blockInfo = Rlp.Decode<BlockInfo?>(ref decoderContext, RlpBehaviors.AllowExtraBytes);
                if (blockInfo is not null)
                {
                    blockInfos.Add(blockInfo);
                }
            }

            if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
            {
                decoderContext.Check(lastCheck);
            }

            ChainLevelInfo info = new(hasMainChainBlock, blockInfos.ToArray());
            return info;
        }

        public Rlp Encode(ChainLevelInfo? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                return Rlp.OfEmptySequence;
            }

            for (int i = 0; i < item.BlockInfos.Length; i++)
            {
                if (item.BlockInfos[i] is null)
                {
                    throw new InvalidOperationException($"{nameof(BlockInfo)} is null when encoding {nameof(ChainLevelInfo)}");
                }
            }

            Rlp[] elements = new Rlp[2];
            elements[0] = Rlp.Encode(item.HasBlockOnMainChain);
            elements[1] = Rlp.Encode(item.BlockInfos);
            Rlp rlp = Rlp.Encode(elements);

            return rlp;
        }

        public int GetLength(ChainLevelInfo item, RlpBehaviors rlpBehaviors)
        {
            throw new NotImplementedException();
        }
    }
}
