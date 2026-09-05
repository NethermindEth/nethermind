// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Serialization.Rlp
{
    [Rlp.SkipGlobalRegistration]
    public class CompactLogEntryDecoder : RlpDecoder<LogEntry?>
    {
        private static readonly RlpLimit RlpLimit = RlpLimit.For<LogEntry>((int)16.MB, nameof(LogEntry));
        private static readonly RlpLimit LogEntryDataRlpLimit = RlpLimit.For<LogEntry>(RlpLimit.DefaultLimit.Limit, nameof(LogEntry.Data));
        public static CompactLogEntryDecoder Instance { get; } = new();

        protected override LogEntry? DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            ReadOnlySpan<byte> rlp = decoderContext.Data;
            int position = decoderContext.Position;

            if (rlp[position] == Rlp.EmptyListByte)
            {
                decoderContext.Position = position + 1;
                return null;
            }

            position = RlpHelpers.ReadSequenceLength(rlp, position, out int logEntryLength);
            Rlp.GuardLimit(logEntryLength, rlp.Length - position, RlpLimit);
            int logEntryCheck = position + logEntryLength;

            position = RlpHelpers.DecodeAddress(rlp, position, allowNull: false, out Address? address);
            position = RlpHelpers.ReadSequenceLength(rlp, position, out int topicsLength);
            int topicCount = topicsLength / Rlp.LengthOfKeccakRlp;
            Rlp.GuardLimit(topicCount, rlp.Length - position, RlpLimit.L4);
            int untilPosition = position + topicsLength;

            using ArrayPoolListRef<Hash256> topics = new(topicCount);
            while (position < untilPosition)
            {
                position = RlpHelpers.DecodeZeroPrefixKeccak(rlp, position, out Hash256? topic);
                topics.Add(topic ?? RlpHelpers.ThrowNullDecodedValue<Hash256>());
            }

            decoderContext.Position = position;
            decoderContext.Check(untilPosition);

            byte[] data = DecodeCompactData(ref decoderContext);
            decoderContext.Check(logEntryCheck);

            return new LogEntry(address!, data, topics.ToArray());
        }

        public static void DecodeLogEntryStructRef(scoped ref RlpReader decoderContext, RlpBehaviors behaviors, out LogEntryStructRef item)
        {
            if (decoderContext.IsNextItemEmptyList())
            {
                decoderContext.ReadByte();
                item = default;
                return;
            }

            int logEntryLength = decoderContext.ReadSequenceLength();
            decoderContext.GuardLimit(logEntryLength, RlpLimit);
            int logEntryCheck = decoderContext.Position + logEntryLength;
            decoderContext.DecodeAddressStructRefNonNull(out AddressStructRef address);
            (int PrefixLength, int ContentLength) = decoderContext.PeekPrefixAndContentLength();
            int sequenceLength = PrefixLength + ContentLength;
            ReadOnlySpan<byte> topics = decoderContext.Data.Slice(decoderContext.Position, sequenceLength);
            decoderContext.SkipItem();

            byte[] data = DecodeCompactData(ref decoderContext);
            decoderContext.Check(logEntryCheck);

            item = new LogEntryStructRef(address, data, topics);
        }

        public static Hash256[] DecodeTopics(RlpReader reader)
        {
            int sequenceLength = reader.ReadSequenceLength();
            int untilPosition = reader.Position + sequenceLength;
            using ArrayPoolListRef<Hash256> topics = new(sequenceLength * 2 / Rlp.LengthOfKeccakRlp);
            while (reader.Position < untilPosition)
            {
                topics.Add(reader.DecodeZeroPrefixKeccakNonNull());
            }
            reader.Check(untilPosition);

            return topics.ToArray();
        }

        public override void Encode<TWriter>(ref TWriter writer, LogEntry? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                writer.WriteByte(Rlp.EmptyListByte);
                return;
            }

            (int total, int topics) = GetContentLength(item);
            writer.StartSequence(total);

            writer.Encode(item.Address);
            writer.StartSequence(topics);

            for (int i = 0; i < item.Topics.Length; i++)
            {
                writer.Encode(item.Topics[i].Bytes.WithoutLeadingZerosOrEmpty());
            }

            ReadOnlySpan<byte> withoutLeadingZero = item.Data.WithoutLeadingZerosOrEmpty();
            int dataZeroPrefix = item.Data.Length - withoutLeadingZero.Length;
            writer.Encode(dataZeroPrefix);
            writer.Encode(withoutLeadingZero);
        }

        public override int GetLength(LogEntry? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                return 1;
            }

            return Rlp.LengthOfSequence(GetContentLength(item).Total);
        }

        private static byte[] DecodeCompactData(scoped ref RlpReader decoderContext)
        {
            int zeroPrefix = decoderContext.DecodePositiveInt();
            ReadOnlySpan<byte> rlpData = decoderContext.DecodeByteArraySpan();

            Rlp.GuardLimit(zeroPrefix, LogEntryDataRlpLimit.Limit - rlpData.Length, LogEntryDataRlpLimit);

            byte[] data = new byte[zeroPrefix + rlpData.Length];
            rlpData.CopyTo(data.AsSpan(zeroPrefix));
            return data;
        }

        private static (int Total, int Topics) GetContentLength(LogEntry? item)
        {
            int contentLength = 0;
            if (item is null)
            {
                return (contentLength, 0);
            }

            contentLength += Rlp.LengthOf(item.Address);

            int topicsLength = GetTopicsLength(item);
            contentLength += Rlp.LengthOfSequence(topicsLength);

            ReadOnlySpan<byte> withoutLeadingZero = item.Data.WithoutLeadingZerosOrEmpty();
            int dataZeroPrefix = item.Data.Length - withoutLeadingZero.Length;
            contentLength += Rlp.LengthOf(dataZeroPrefix);
            contentLength += Rlp.LengthOf(withoutLeadingZero);

            return (contentLength, topicsLength);
        }

        private static int GetTopicsLength(LogEntry? item)
        {
            if (item is null)
            {
                return 0;
            }

            int topicsLength = 0;
            for (int i = 0; i < item.Topics.Length; i++)
            {
                topicsLength += Rlp.LengthOf(item.Topics[i].Bytes.WithoutLeadingZerosOrEmpty());
            }

            return topicsLength;
        }
    }
}
