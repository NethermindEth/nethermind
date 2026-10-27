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
        public override void PopulateLength(ChainLevelInfo? item, ref LengthStack lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                lengths.Push(Rlp.OfEmptyList.Length);
                return;
            }

            foreach (BlockInfo? blockInfo in item.BlockInfos)
            {
                if (blockInfo is null)
                {
                    ThrowHasNull();
                }
            }

            int contentLength = Rlp.LengthOf(item.HasBlockOnMainChain)
                + BlockInfoDecoder.Instance.PopulateSequenceAndPeek(item.BlockInfos, ref lengths);
            lengths.Push(contentLength);
            lengths.Push(Rlp.LengthOfSequence(contentLength));

            [StackTraceHidden, DoesNotReturn]
            static void ThrowHasNull()
                => throw new InvalidOperationException($"{nameof(BlockInfo)} is null when encoding {nameof(ChainLevelInfo)}");
        }

        public override void Encode<TWriter>(ChainLevelInfo? item, ref LengthStack lengths, ref TWriter writer, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            lengths.Pop();
            if (item is null)
            {
                writer.EncodeNullObject();
                return;
            }

            writer.StartSequence(lengths.Pop());
            writer.Encode(item.HasBlockOnMainChain);
            BlockInfoDecoder.Instance.EncodeSequence(item.BlockInfos, ref lengths, ref writer);
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

    }
}
