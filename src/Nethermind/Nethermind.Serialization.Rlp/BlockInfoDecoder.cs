// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.FluentRlp;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Nethermind.Serialization.Rlp
{
    [method: DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(BlockInfoDecoder))]
    public sealed class BlockInfoDecoder() : RlpDecoder<BlockInfo>
    {
        public static BlockInfoDecoder Instance { get; } = new();

        public override void Encode(ref RlpWriter writer, BlockInfo? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                writer.EncodeNullObject();
                return;
            }

            int contentLength = GetContentLength(item, rlpBehaviors);

            bool hasMetadata = item.Metadata != BlockMetadata.None;
            RlpSequence sequence = writer.StartSequence(contentLength);
            writer.Encode(item.BlockHash);
            writer.Encode(item.WasProcessed);
            writer.Encode(item.TotalDifficulty);
            if (hasMetadata)
            {
                writer.Encode((int)item.Metadata);
            }

            writer.EndSequence(sequence);
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

        protected override BlockInfo? DecodeInternal(ref RlpReader reader, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
            => (rlpBehaviors & RlpBehaviors.TrustedInput) != 0 ? Decode<OffFlag>(ref reader, rlpBehaviors) : Decode<OnFlag>(ref reader, rlpBehaviors);

        // Not inlined into the decode entry, so that its reads keep the inline budget.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static BlockInfo? Decode<TCanonicalChecks>(ref RlpReader reader, RlpBehaviors rlpBehaviors) where TCanonicalChecks : struct, IFlag
        {
            if (reader.TryReadEmptySequence()) return null;

            RlpReader fields = reader.EnterSequence<TCanonicalChecks>(out int itemLength);
            Hash256? blockHash = fields.DecodeKeccakOrNull();
            bool wasProcessed = fields.DecodeBool();
            UInt256 totalDifficulty = fields.DecodeUInt256<TCanonicalChecks>();

            BlockMetadata metadata = BlockMetadata.None;
            // if we hadn't reached the end of the stream, assume we have metadata to decode
            if (fields.HasNext)
            {
                metadata = (BlockMetadata)fields.DecodeUInt<TCanonicalChecks>();
            }

            reader.ExitSequence(in fields, itemLength, rlpBehaviors);

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
