// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Collections;

namespace Nethermind.Serialization.Rlp
{
    [method: DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(ChainLevelDecoder))]
    public sealed class ChainLevelDecoder() : RlpDecoder<ChainLevelInfo>
    {
        public override void Encode<TWriter>(ref TWriter writer, ChainLevelInfo? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                writer.EncodeNullObject();
                return;
            }

            foreach (BlockInfo? blockInfo in item.BlockInfos)
            {
                if (blockInfo is null)
                {
                    ThrowHasNull();
                }
            }

            int infoLength = GetBlockInfoLength(item.BlockInfos);
            writer.StartSequence(GetContentLength(item, infoLength));
            writer.Encode(item.HasBlockOnMainChain);
            writer.StartSequence(infoLength);
            BlockInfoDecoder blockInfoDecoder = BlockInfoDecoder.Instance;
            foreach (BlockInfo? blockInfo in item.BlockInfos)
            {
                blockInfoDecoder.Encode(ref writer, blockInfo);
            }

            [StackTraceHidden, DoesNotReturn]
            static void ThrowHasNull()
                => throw new InvalidOperationException($"{nameof(BlockInfo)} is null when encoding {nameof(ChainLevelInfo)}");
        }

        protected override ChainLevelInfo? DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (decoderContext.TryConsumeNull(out LiteRlpReader rlp, out int position)) return null;

            rlp.ReadSequenceLength(ref position, out int sequenceLength);
            int lastCheck = position + sequenceLength;
            rlp.DecodeBool(ref position, out bool hasMainChainBlock);
            rlp.ReadSequenceLength(ref position, out _);
            decoderContext.Position = position;

            using ArrayPoolListRef<BlockInfo> blockInfos = new(0);
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
                RlpHelpers.Check(decoderContext.Position, lastCheck);
            }

            ChainLevelInfo info = new(hasMainChainBlock, blockInfos.ToArray());
            return info;
        }

        private static int GetContentLength(ChainLevelInfo item, int infoLength)
            => Rlp.LengthOf(item.HasBlockOnMainChain) + Rlp.LengthOfSequence(infoLength);

        public override int GetLength(ChainLevelInfo? item, RlpBehaviors rlpBehaviors)
        {
            if (item is null)
            {
                return Rlp.OfEmptyList.Length;
            }

            return Rlp.LengthOfSequence(GetContentLength(item, GetBlockInfoLength(item.BlockInfos)));
        }

        private static int GetBlockInfoLength(BlockInfo[] item)
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
