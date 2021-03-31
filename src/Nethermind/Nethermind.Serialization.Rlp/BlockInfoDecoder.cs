//  Copyright (c) 2021 Demerzel Solutions Limited
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
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Serialization.Rlp
{
    public class BlockInfoDecoder : IRlpStreamDecoder<BlockInfo>, IRlpValueDecoder<BlockInfo>
    {
        private readonly bool _chainWithFinalization;

        public BlockInfoDecoder(bool chainWithFinalization)
        {
            _chainWithFinalization = chainWithFinalization;
        }

        // ReSharper disable once UnusedMember.Global this is needed for auto-registration
        public BlockInfoDecoder() : this(false) { }
        
        public BlockInfo? Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (rlpStream.IsNextItemNull())
            {
                rlpStream.ReadByte();
                return null;
            }
            
            int lastCheck = rlpStream.ReadSequenceLength() + rlpStream.Position;

            Keccak? blockHash = rlpStream.DecodeKeccak();

            bool wasProcessed = rlpStream.DecodeBool();
            UInt256 totalDifficulty = rlpStream.DecodeUInt256();

            bool isFinalized = false;
            if (_chainWithFinalization)
            {
                isFinalized = rlpStream.DecodeBool();
            }

            if ((rlpBehaviors & RlpBehaviors.AllowExtraData) != RlpBehaviors.AllowExtraData)
            {
                rlpStream.Check(lastCheck);
            }

            if (blockHash is null)
            {
                return null;
            }
            
            BlockInfo blockInfo = new(blockHash, totalDifficulty)
            {
                WasProcessed = wasProcessed,
                IsFinalized = isFinalized
            };

            return blockInfo;
        }

        public void Encode(RlpStream stream, BlockInfo item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            throw new NotImplementedException();
        }

        public Rlp Encode(BlockInfo? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
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
            throw new NotImplementedException();
        }

        public BlockInfo? Decode(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (decoderContext.IsNextItemNull())
            {
                decoderContext.ReadByte();
                return null;
            }
            
            int lastCheck = decoderContext.ReadSequenceLength() + decoderContext.Position;

            Keccak? blockHash = decoderContext.DecodeKeccak();
            bool wasProcessed = decoderContext.DecodeBool();
            UInt256 totalDifficulty = decoderContext.DecodeUInt256();
            bool isFinalized = false;

            if (_chainWithFinalization)
            {
                isFinalized = decoderContext.DecodeBool();
            }

            if ((rlpBehaviors & RlpBehaviors.AllowExtraData) != RlpBehaviors.AllowExtraData)
            {
                decoderContext.Check(lastCheck);
            }

            if (blockHash is null)
            {
                return null;
            }
            
            BlockInfo blockInfo = new(blockHash, totalDifficulty)
            {
                WasProcessed = wasProcessed,
                IsFinalized = isFinalized
            };
            
            return blockInfo;
        }
    }
}
