// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Serialization.Rlp
{
    [Rlp.SkipGlobalRegistration]
    public class CompactLogEntryDecoder : IRlpDecoder<LogEntry>
    {
        public static CompactLogEntryDecoder Instance { get; } = new();

        public LogEntry? Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (rlpStream.IsNextItemNull())
            {
                rlpStream.ReadByte();
                return null;
            }

            rlpStream.ReadSequenceLength();
            Address? address = rlpStream.DecodeAddress();
            long sequenceLength = rlpStream.ReadSequenceLength();
            long untilPosition = rlpStream.Position + sequenceLength;
            using ArrayPoolList<Keccak> topics = new((int)(sequenceLength * 2 / Rlp.LengthOfKeccakRlp));
            while (rlpStream.Position < untilPosition)
            {
                topics.Add(rlpStream.DecodeZeroPrefixKeccak());
            }

            int zeroPrefix = rlpStream.DecodeInt();
            ReadOnlySpan<byte> rlpData = rlpStream.DecodeByteArraySpan();
            byte[] data = new byte[zeroPrefix + rlpData.Length];
            rlpData.CopyTo(data.AsSpan(zeroPrefix));

            return new LogEntry(address, data, topics.ToArray());
        }

        public LogEntry? Decode(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (decoderContext.IsNextItemNull())
            {
                decoderContext.ReadByte();
                return null;
            }

            decoderContext.ReadSequenceLength();
            Address? address = decoderContext.DecodeAddress();
            long sequenceLength = decoderContext.ReadSequenceLength();
            long untilPosition = decoderContext.Position + sequenceLength;
            using ArrayPoolList<Keccak> topics = new((int)(sequenceLength * 2 / Rlp.LengthOfKeccakRlp));
            while (decoderContext.Position < untilPosition)
            {
                topics.Add(decoderContext.DecodeZeroPrefixKeccak());
            }

            int zeroPrefix = decoderContext.DecodeInt();
            ReadOnlySpan<byte> rlpData = decoderContext.DecodeByteArraySpan();
            byte[] data = new byte[zeroPrefix + rlpData.Length];
            rlpData.CopyTo(data.AsSpan(zeroPrefix));

            return new LogEntry(address, data, topics.ToArray());
        }

        public void DecodeLogEntryStructRef(scoped ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors behaviors, out LogEntryStructRef item)
        {
            if (decoderContext.IsNextItemNull())
            {
                decoderContext.ReadByte();
                item = new LogEntryStructRef();
                return;
            }

            decoderContext.ReadSequenceLength();
            decoderContext.DecodeAddressStructRef(out var address);
            var peekPrefixAndContentLength = decoderContext.PeekPrefixAndContentLength();
            var sequenceLength = peekPrefixAndContentLength.PrefixLength + peekPrefixAndContentLength.ContentLength;
            var topics = decoderContext.Data.Slice(decoderContext.Position, sequenceLength);
            decoderContext.SkipItem();

            int zeroPrefix = decoderContext.DecodeInt();
            ReadOnlySpan<byte> rlpData = decoderContext.DecodeByteArraySpan();
            byte[] data = new byte[zeroPrefix + rlpData.Length];
            rlpData.CopyTo(data.AsSpan(zeroPrefix));

            item = new LogEntryStructRef(address, data, topics);
        }

        public Keccak[] DecodeTopics(Rlp.ValueDecoderContext valueDecoderContext)
        {
            long sequenceLength = valueDecoderContext.ReadSequenceLength();
            long untilPosition = valueDecoderContext.Position + sequenceLength;
            using ArrayPoolList<Keccak> topics = new((int)(sequenceLength * 2 / Rlp.LengthOfKeccakRlp));
            while (valueDecoderContext.Position < untilPosition)
            {
                topics.Add(valueDecoderContext.DecodeZeroPrefixKeccak());
            }

            return topics.ToArray();
        }

        public void Encode(RlpStream rlpStream, LogEntry? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                rlpStream.EncodeNullObject();
                return;
            }

            var (total, topics) = GetContentLength(item);
            rlpStream.StartSequence(total);

            rlpStream.Encode(item.LoggersAddress);
            rlpStream.StartSequence(topics);

            for (var i = 0; i < item.Topics.Length; i++)
            {
                rlpStream.Encode(item.Topics[i].Bytes.WithoutLeadingZerosOrEmpty());
            }

            Span<byte> withoutLeadingZero = item.Data.WithoutLeadingZerosOrEmpty();
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
            var contentLength = 0;
            if (item is null)
            {
                return (contentLength, 0);
            }

            contentLength += Rlp.LengthOf(item.LoggersAddress);

            int topicsLength = GetTopicsLength(item);
            contentLength += Rlp.LengthOfSequence(topicsLength);

            Span<byte> withoutLeadingZero = item.Data.WithoutLeadingZerosOrEmpty();
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
