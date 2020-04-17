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

using Nethermind.Core;

namespace Nethermind.Serialization.Rlp
{
    public class BlockInfoDecoder : IRlpDecoder<BlockInfo>, IRlpValueDecoder<BlockInfo>
    {
        private readonly bool _chainWithFinalization;

        public BlockInfoDecoder(bool chainWithFinalization)
        {
            _chainWithFinalization = chainWithFinalization;
        }

        // ReSharper disable once UnusedMember.Global this is needed for auto-registration
        public BlockInfoDecoder() : this(false) { }
        
        public BlockInfo Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (rlpStream.IsNextItemNull())
            {
                rlpStream.ReadByte();
                return null;
            }
            
            int lastCheck = rlpStream.ReadSequenceLength() + rlpStream.Position;

            BlockInfo blockInfo = new BlockInfo
            {
                BlockHash = rlpStream.DecodeKeccak(),
                WasProcessed = rlpStream.DecodeBool(),
                TotalDifficulty = rlpStream.DecodeUInt256()
            };

            if (_chainWithFinalization)
            {
                blockInfo.IsFinalized = rlpStream.DecodeBool();
            }

            if ((rlpBehaviors & RlpBehaviors.AllowExtraData) != RlpBehaviors.AllowExtraData)
            {
                rlpStream.Check(lastCheck);
            }

            return blockInfo;
        }

        public Rlp Encode(BlockInfo item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                return Rlp.OfEmptySequence;
            }
            
            Rlp[] elements = new Rlp[_chainWithFinalization ? 4 : 3];
            elements[0] = Rlp.Encode(item.BlockHash);
            elements[1] = Rlp.Encode(item.WasProcessed);
            elements[2] = Rlp.Encode(item.TotalDifficulty);
            
            if (_chainWithFinalization)
            {
                elements[3] = Rlp.Encode(item.IsFinalized);
            }

            return Rlp.Encode(elements);
        }

        public int GetLength(BlockInfo item, RlpBehaviors rlpBehaviors)
        {
            throw new System.NotImplementedException();
        }

        public BlockInfo Decode(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (decoderContext.IsNextItemNull())
            {
                decoderContext.ReadByte();
                return null;
            }
            
            int lastCheck = decoderContext.ReadSequenceLength() + decoderContext.Position;

            BlockInfo blockInfo = new BlockInfo
            {
                BlockHash = decoderContext.DecodeKeccak(),
                WasProcessed = decoderContext.DecodeBool(),
                TotalDifficulty = decoderContext.DecodeUInt256()
            };

            if (_chainWithFinalization)
            {
                blockInfo.IsFinalized = decoderContext.DecodeBool();
            }

            if ((rlpBehaviors & RlpBehaviors.AllowExtraData) != RlpBehaviors.AllowExtraData)
            {
                decoderContext.Check(lastCheck);
            }

            return blockInfo;
        }

        public Rlp Encode(ChainLevelInfo item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            throw new System.NotImplementedException();
        }

        public int GetLength(ChainLevelInfo item, RlpBehaviors rlpBehaviors)
        {
            throw new System.NotImplementedException();
        }
    }
}