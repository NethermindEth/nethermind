// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

        public void Encode(RlpStream stream, BlockInfo? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                stream.Encode(Rlp.OfEmptySequence);
                return;
            }

            int contentLength = GetContentLength(item, rlpBehaviors);

            bool hasMetadata = item.Metadata != BlockMetadata.None;
            stream.StartSequence(contentLength);
            stream.Encode(item.BlockHash);
            stream.Encode(item.WasProcessed);
            stream.Encode(item.TotalDifficulty);
            if (hasMetadata)
            {
                stream.Encode((int)item.Metadata);
            }
        }

        private int GetContentLength(BlockInfo item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            bool hasMetadata = item.Metadata != BlockMetadata.None;
            int contentLength = 0;
            contentLength += Rlp.LengthOf(item.BlockHash);
            contentLength += Rlp.LengthOf(item.WasProcessed);
            contentLength += Rlp.LengthOf(item.TotalDifficulty);

            if (hasMetadata)
            {
                contentLength += Rlp.LengthOf((int)item.Metadata);
            }

            return contentLength;
        }

        public int GetLength(BlockInfo? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            return item == null ? Rlp.OfEmptySequence.Length : Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));
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
