// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using System;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Serialization.Rlp
{
    [method: DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(LogEntryDecoder))]
    public sealed class LogEntryDecoder() : RlpDecoder<LogEntry?>
    {
        private static readonly RlpLimit RlpLimit = RlpLimit.For<LogEntry>((int)16.MB, nameof(LogEntry));
        public static LogEntryDecoder Instance { get; } = new();

        protected override LogEntry? DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (decoderContext.IsNextItemEmptyList())
            {
                decoderContext.ReadByte();
                return null;
            }

            int logEntryLength = decoderContext.ReadSequenceLength();
            decoderContext.GuardLimit(logEntryLength, RlpLimit);
            int logEntryCheck = decoderContext.Position + logEntryLength;
            Address address = decoderContext.DecodeAddressNonNull();
            int topicsLength = decoderContext.ReadSequenceLength();
            int topicsCheck = decoderContext.Position + topicsLength;
            int topicCount = topicsLength / Rlp.LengthOfKeccakRlp;
            decoderContext.GuardLimit(topicCount, RlpLimit.L4);
            Hash256[] topics = new Hash256[topicCount];
            for (int i = 0; i < topics.Length; i++)
            {
                topics[i] = decoderContext.DecodeKeccakNonNull();
            }
            decoderContext.Check(topicsCheck);

            byte[] data = decoderContext.DecodeByteArray();
            decoderContext.Check(logEntryCheck);

            return new LogEntry(address, data, topics);
        }

        public override void Encode<TWriter>(ref TWriter writer, LogEntry? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                writer.EncodeNullObject();
                return;
            }

            (int total, int topics) = GetContentLength(item);
            writer.StartSequence(total);

            writer.Encode(item.Address);
            writer.StartSequence(topics);

            for (int i = 0; i < item.Topics.Length; i++)
            {
                writer.Encode(item.Topics[i]);
            }

            writer.Encode(item.Data);
        }

        public override int GetLength(LogEntry? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
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
            contentLength += Rlp.LengthOf(item.Data);

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
                topicsLength += Rlp.LengthOf(item.Topics[i]);
            }

            return topicsLength;
        }

        public static void DecodeStructRef(scoped ref RlpReader decoderContext, RlpBehaviors storage, out LogEntryStructRef item)
        {
            if (decoderContext.IsNextItemEmptyList())
            {
                decoderContext.ReadByte();
                throw new RlpException("LogEntry decoded as null");
            }

            int logEntryLength = decoderContext.ReadSequenceLength();
            int logEntryCheck = decoderContext.Position + logEntryLength;
            decoderContext.DecodeAddressStructRef(out AddressStructRef address);
            (int prefixLength, int contentLength) = decoderContext.PeekPrefixAndContentLength();
            int sequenceLength = prefixLength + contentLength;
            ReadOnlySpan<byte> topics = decoderContext.Data.Slice(decoderContext.Position, sequenceLength);
            decoderContext.SkipItem();
            ReadOnlySpan<byte> data = decoderContext.DecodeByteArraySpan();
            decoderContext.Check(logEntryCheck);

            item = new LogEntryStructRef(address, data, topics);
        }
    }
}
