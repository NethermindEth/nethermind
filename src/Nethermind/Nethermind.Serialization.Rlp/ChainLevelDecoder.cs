// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
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
                rlpStream.ReadByte();
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

        public void Encode(RlpStream stream, ChainLevelInfo? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                stream.Encode(Rlp.OfEmptySequence);
                return;
            }

            if (item.BlockInfos.Any(t => t == null))
            {
                throw new InvalidOperationException($"{nameof(BlockInfo)} is null when encoding {nameof(ChainLevelInfo)}");
            }

            int contentLength = GetContentLength(item, rlpBehaviors);
            stream.StartSequence(contentLength);
            stream.Encode(item.HasBlockOnMainChain);
            int infoLength = GetBlockInfoLength(item.BlockInfos);
            stream.StartSequence(infoLength);
            foreach (BlockInfo? blockInfo in item.BlockInfos)
            {
                stream.Encode(blockInfo);
            }
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
            throw new NotImplementedException();
        }

        private int GetContentLength(ChainLevelInfo item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                return Rlp.OfEmptySequence.Length;
            }
            int contentLength = 0;
            contentLength += Rlp.LengthOf(item.HasBlockOnMainChain);
            contentLength += Rlp.LengthOfSequence(GetBlockInfoLength(item.BlockInfos));
            return contentLength;
        }

        public int GetLength(ChainLevelInfo? item, RlpBehaviors rlpBehaviors)
        {
            if (item == null)
            {
                return Rlp.OfEmptySequence.Length;
            }

            int contLength = GetContentLength(item, rlpBehaviors);
            return Rlp.LengthOfSequence(contLength);
        }

        private int GetBlockInfoLength(BlockInfo[] item)
        {
            int contentLength = 0;
            foreach (BlockInfo? blockInfo in item)
            {
                contentLength += Rlp.LengthOf(blockInfo);
            }

            return contentLength;
        }
    }
}
