// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Serialization.Rlp
{
    [method: DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(BlockInfoDecoder))]
    public sealed class BlockInfoDecoder() : RlpDecoder<BlockInfo>
    {
        public static BlockInfoDecoder Instance { get; } = new();

        public override void Encode<TWriter>(ref TWriter writer, BlockInfo? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                writer.EncodeNullObject();
                return;
            }

            int contentLength = GetContentLength(item, rlpBehaviors);

            bool hasMetadata = item.Metadata != BlockMetadata.None;
            writer.StartSequence(contentLength);
            writer.Encode(item.BlockHash);
            writer.Encode(item.WasProcessed);
            writer.Encode(item.TotalDifficulty);
            if (hasMetadata)
            {
                writer.Encode((int)item.Metadata);
            }
        }

        private static int GetContentLength(BlockInfo item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
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

        public override int GetLength(BlockInfo? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None) => item is null ? Rlp.OfEmptyList.Length : Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));

        protected override BlockInfo? DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            ReadOnlySpan<byte> rlp = decoderContext.Data;
            int position = decoderContext.Position;

            if (rlp[position] == Rlp.EmptyListByte)
            {
                decoderContext.Position = position + 1;
                return null;
            }

            position = RlpHelpers.ReadSequenceLength(rlp, position, out int sequenceLength);
            int lastCheck = position + sequenceLength;

            position = RlpHelpers.DecodeKeccakOrNull(rlp, position, out Hash256? blockHash);
            position = RlpHelpers.DecodeBool(rlp, position, out bool wasProcessed);
            position = RlpHelpers.DecodeUInt256(rlp, position, -1, out UInt256 totalDifficulty);

            BlockMetadata metadata = BlockMetadata.None;
            // if we hadn't reached the end of the stream, assume we have metadata to decode
            if (position != lastCheck)
            {
                position = RlpHelpers.DecodeUInt(rlp, position, out uint rawMetadata);
                metadata = (BlockMetadata)rawMetadata;
            }

            decoderContext.Position = position;

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
