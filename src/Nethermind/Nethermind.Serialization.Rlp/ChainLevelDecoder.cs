//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Serialization.Rlp
{
    public class ChainLevelDecoder : IRlpDecoder<ChainLevelInfo>, IRlpValueDecoder<ChainLevelInfo>
    {
        public ChainLevelInfo Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (rlpStream.IsNextItemNull())
            {
                return null;
            }
            
            int lastCheck = rlpStream.ReadSequenceLength() + rlpStream.Position;
            bool hasMainChainBlock = rlpStream.DecodeBool();

            List<BlockInfo> blockInfos = new List<BlockInfo>();

            rlpStream.ReadSequenceLength();
            while (rlpStream.Position < lastCheck)
            {
                blockInfos.Add(Rlp.Decode<BlockInfo>(rlpStream, RlpBehaviors.AllowExtraData));
            }

            if ((rlpBehaviors & RlpBehaviors.AllowExtraData) != RlpBehaviors.AllowExtraData)
            {
                rlpStream.Check(lastCheck);
            }

            ChainLevelInfo info = new ChainLevelInfo(hasMainChainBlock, blockInfos.ToArray());
            return info;
        }

        public ChainLevelInfo Decode(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (decoderContext.IsNextItemNull())
            {
                return null;
            }
            
            int lastCheck = decoderContext.ReadSequenceLength() + decoderContext.Position;
            bool hasMainChainBlock = decoderContext.DecodeBool();

            List<BlockInfo> blockInfos = new List<BlockInfo>();

            decoderContext.ReadSequenceLength();
            while (decoderContext.Position < lastCheck)
            {
                blockInfos.Add(Rlp.Decode<BlockInfo>(ref decoderContext, RlpBehaviors.AllowExtraData));
            }

            if ((rlpBehaviors & RlpBehaviors.AllowExtraData) != RlpBehaviors.AllowExtraData)
            {
                decoderContext.Check(lastCheck);
            }

            ChainLevelInfo info = new ChainLevelInfo(hasMainChainBlock, blockInfos.ToArray());
            return info;
        }

        public Rlp Encode(ChainLevelInfo item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                return Rlp.OfEmptySequence;
            }

            for (int i = 0; i < item.BlockInfos.Length; i++)
            {
                if (item.BlockInfos[i] == null)
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
            throw new System.NotImplementedException();
        }
    }
}