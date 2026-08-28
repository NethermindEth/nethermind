// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;

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

            if (item.BlockInfos.AsSpan().Contains(null))
            {
                ThrowHasNull();
            }

            int contentLength = GetContentLength(item, rlpBehaviors);
            writer.StartSequence(contentLength);
            writer.Encode(item.HasBlockOnMainChain);
            int infoLength = GetBlockInfoLength(item.BlockInfos);
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
            if (decoderContext.IsNextItemEmptyList())
            {
                decoderContext.ReadByte();
                return null;
            }

            int lastCheck = decoderContext.ReadSequenceLength() + decoderContext.Position;
            bool hasMainChainBlock = decoderContext.DecodeBool();

            List<BlockInfo> blockInfos = [];

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

        private static int GetContentLength(ChainLevelInfo item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                return Rlp.OfEmptyList.Length;
            }
            int contentLength = 0;
            contentLength += Rlp.LengthOf(item.HasBlockOnMainChain);
            contentLength += Rlp.LengthOfSequence(GetBlockInfoLength(item.BlockInfos));
            return contentLength;
        }

        public override int GetLength(ChainLevelInfo? item, RlpBehaviors rlpBehaviors)
        {
            if (item is null)
            {
                return Rlp.OfEmptyList.Length;
            }

            int contLength = GetContentLength(item, rlpBehaviors);
            return Rlp.LengthOfSequence(contLength);
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
