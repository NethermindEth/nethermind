// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Serialization.FluentRlp;

namespace Nethermind.Serialization.Rlp
{
    [method: DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(ChainLevelDecoder))]
    public sealed class ChainLevelDecoder() : RlpDecoder<ChainLevelInfo>
    {
        public override void Encode(ref RlpWriter writer, ChainLevelInfo? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
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
            RlpSequence sequence = writer.StartSequence(GetContentLength(item, infoLength));
            writer.Encode(item.HasBlockOnMainChain);
            RlpSequence infos = writer.StartSequence(infoLength);
            BlockInfoDecoder blockInfoDecoder = BlockInfoDecoder.Instance;
            foreach (BlockInfo? blockInfo in item.BlockInfos)
            {
                blockInfoDecoder.Encode(ref writer, blockInfo);
            }

            writer.EndSequence(infos);
            writer.EndSequence(sequence);

            [StackTraceHidden, DoesNotReturn]
            static void ThrowHasNull()
                => throw new InvalidOperationException($"{nameof(BlockInfo)} is null when encoding {nameof(ChainLevelInfo)}");
        }

        protected override ChainLevelInfo? DecodeInternal(ref RlpReader reader, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
            => (rlpBehaviors & RlpBehaviors.TrustedInput) != 0 ? Decode<OffFlag>(ref reader, rlpBehaviors) : Decode<OnFlag>(ref reader, rlpBehaviors);

        private static ChainLevelInfo? Decode<TCanonicalChecks>(ref RlpReader reader, RlpBehaviors rlpBehaviors) where TCanonicalChecks : struct, IFlag
        {
            if (reader.TryReadEmptySequence()) return null;

            RlpReader fields = reader.EnterSequence<TCanonicalChecks>(out int itemLength);
            bool hasMainChainBlock = fields.DecodeBool();
            RlpReader items = fields.EnterSequence<TCanonicalChecks>(out int itemsLength);

            BlockInfo[] blockInfos = new BlockInfo[items.CountItems<TCanonicalChecks>(int.MaxValue)];
            RlpBehaviors infoBehaviors = RlpBehaviors.AllowExtraBytes | (rlpBehaviors & RlpBehaviors.TrustedInput);
            int count = DecodeBlockInfos(ref items, blockInfos, 0, infoBehaviors);
            fields.ExitSequence(in items, itemsLength);
            if (fields.HasNext)
            {
                // Items after the list also decode as block infos, so that a level reads as in earlier versions.
                Array.Resize(ref blockInfos, count + fields.CountItems<TCanonicalChecks>(int.MaxValue));
                count = DecodeBlockInfos(ref fields, blockInfos, count, infoBehaviors);
            }

            reader.ExitSequence(in fields, itemLength);

            if (count != blockInfos.Length) Array.Resize(ref blockInfos, count);
            return new ChainLevelInfo(hasMainChainBlock, blockInfos);
        }

        private static int DecodeBlockInfos(ref RlpReader items, BlockInfo[] blockInfos, int count, RlpBehaviors infoBehaviors)
        {
            while (items.HasNext)
            {
                // block info can be null for corrupted states (also cases where block hash is null from the old DBs)
                BlockInfo? blockInfo = BlockInfoDecoder.Instance.Decode(ref items, infoBehaviors);
                if (blockInfo is not null)
                {
                    blockInfos[count++] = blockInfo;
                }
            }

            return count;
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
