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
    public class CompactLogEntryDecoder : IRlpDecoder<LogEntry>
    {
        private static readonly RlpLimit RlpLimit = RlpLimit.For<LogEntry>((int)16.MB, nameof(LogEntry));
        public static CompactLogEntryDecoder Instance { get; } = new();

        public static LogEntry? Decode(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (decoderContext.IsNextItemEmptyList())
            {
                decoderContext.ReadByte();
                return null;
            }

            int logEntryLength = decoderContext.ReadSequenceLength();
            decoderContext.GuardLimit(logEntryLength, RlpLimit);
            int logEntryCheck = decoderContext.Position + logEntryLength;
            Address? address = decoderContext.DecodeAddress();
            int topicsLength = decoderContext.ReadSequenceLength();
            int topicCount = topicsLength / Rlp.LengthOfKeccakRlp;
            decoderContext.GuardLimit(topicCount, RlpLimit.L4);
            int untilPosition = decoderContext.Position + topicsLength;
            using ArrayPoolListRef<Hash256> topics = new(topicCount);
            while (decoderContext.Position < untilPosition)
            {
                topics.Add(decoderContext.DecodeZeroPrefixKeccak());
            }
            decoderContext.Check(untilPosition);

            int zeroPrefix = decoderContext.DecodeInt();
            ReadOnlySpan<byte> rlpData = decoderContext.DecodeByteArraySpan();
            byte[] data = new byte[zeroPrefix + rlpData.Length];
            rlpData.CopyTo(data.AsSpan(zeroPrefix));
            decoderContext.Check(logEntryCheck);

            return new LogEntry(address, data, topics.ToArray());
        }

        public static void DecodeLogEntryStructRef(scoped ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors behaviors, out LogEntryStructRef item)
        {
            if (decoderContext.IsNextItemEmptyList())
            {
                decoderContext.ReadByte();
                item = new LogEntryStructRef();
                return;
            }

            int logEntryLength = decoderContext.ReadSequenceLength();
            int logEntryCheck = decoderContext.Position + logEntryLength;
            decoderContext.DecodeAddressStructRef(out AddressStructRef address);
            (int PrefixLength, int ContentLength) = decoderContext.PeekPrefixAndContentLength();
            int sequenceLength = PrefixLength + ContentLength;
            ReadOnlySpan<byte> topics = decoderContext.Data.Slice(decoderContext.Position, sequenceLength);
            decoderContext.SkipItem();

            int zeroPrefix = decoderContext.DecodeInt();
            ReadOnlySpan<byte> rlpData = decoderContext.DecodeByteArraySpan();
            byte[] data = new byte[zeroPrefix + rlpData.Length];
            rlpData.CopyTo(data.AsSpan(zeroPrefix));
            decoderContext.Check(logEntryCheck);

            item = new LogEntryStructRef(address, data, topics);
        }

        public static Hash256[] DecodeTopics(Rlp.ValueDecoderContext valueDecoderContext)
        {
            int sequenceLength = valueDecoderContext.ReadSequenceLength();
            int untilPosition = valueDecoderContext.Position + sequenceLength;
            using ArrayPoolListRef<Hash256> topics = new(sequenceLength * 2 / Rlp.LengthOfKeccakRlp);
            while (valueDecoderContext.Position < untilPosition)
            {
                topics.Add(valueDecoderContext.DecodeZeroPrefixKeccak());
            }
            valueDecoderContext.Check(untilPosition);

            return topics.ToArray();
        }

        public static void Encode(RlpStream rlpStream, LogEntry? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                rlpStream.EncodeNullObject();
                return;
            }

            (int total, int topics) = GetContentLength(item);
            rlpStream.StartSequence(total);

            rlpStream.Encode(item.Address);
            rlpStream.StartSequence(topics);

            for (int i = 0; i < item.Topics.Length; i++)
            {
                rlpStream.Encode(item.Topics[i].Bytes.WithoutLeadingZerosOrEmpty());
            }

            ReadOnlySpan<byte> withoutLeadingZero = item.Data.WithoutLeadingZerosOrEmpty();
            int dataZeroPrefix = item.Data.Length - withoutLeadingZero.Length;
            rlpStream.Encode(dataZeroPrefix);
            rlpStream.Encode(withoutLeadingZero);
        }

        public int GetLength(LogEntry? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                return 1;
            }

            return Rlp.LengthOfSequence(GetContentLength(item).Total);
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
