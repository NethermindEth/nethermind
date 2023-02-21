// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Serialization.Rlp
{
    public class BlockInfoDecoder : IRlpStreamDecoder<BlockInfo>, IRlpValueDecoder<BlockInfo>
    {
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

            BlockMetadata metadata = BlockMetadata.None;
            // if we hadn't reached the end of the stream, assume we have metadata to decode
            if (rlpStream.Position != lastCheck)
            {
                metadata = (BlockMetadata)rlpStream.DecodeInt();
            }

            if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
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
                Metadata = metadata,
            };

            return blockInfo;
        }

        public void Encode(RlpStream stream, BlockInfo item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            throw new NotImplementedException();
        }

        public Rlp Encode(BlockInfo? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                return Rlp.OfEmptySequence;
            }

            bool hasMetadata = item.Metadata != BlockMetadata.None;

            Rlp[] elements = new Rlp[hasMetadata ? 4 : 3];
            elements[0] = Rlp.Encode(item.BlockHash);
            elements[1] = Rlp.Encode(item.WasProcessed);
            elements[2] = Rlp.Encode(item.TotalDifficulty);
            if (hasMetadata)
            {
                elements[3] = Rlp.Encode((int)item.Metadata);
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

            BlockMetadata metadata = BlockMetadata.None;
            // if we hadn't reached the end of the stream, assume we have metadata to decode
            if (decoderContext.Position != lastCheck)
            {
                metadata = (BlockMetadata)decoderContext.DecodeInt();
            }

            if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
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
                Metadata = metadata
            };

            return blockInfo;
        }
    }
}
