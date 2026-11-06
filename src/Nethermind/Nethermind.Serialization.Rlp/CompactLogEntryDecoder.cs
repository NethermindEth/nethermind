// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.FluentRlp;

namespace Nethermind.Serialization.Rlp
{
    [Rlp.SkipGlobalRegistration]
    public class CompactLogEntryDecoder : RlpDecoder<LogEntry?>
    {
        private static readonly RlpLimit RlpLimit = RlpLimit.For<LogEntry>((int)16.MB, nameof(LogEntry));
        private static readonly RlpLimit LogEntryDataRlpLimit = RlpLimit.For<LogEntry>(RlpLimit.DefaultLimit.Limit, nameof(LogEntry.Data));
        public static CompactLogEntryDecoder Instance { get; } = new();

        protected override LogEntry? DecodeInternal(ref RlpReader reader, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
            => (rlpBehaviors & RlpBehaviors.TrustedInput) != 0 ? Decode<OffFlag>(ref reader) : Decode<OnFlag>(ref reader);

        internal static LogEntry? Decode<TCanonicalChecks>(ref RlpReader reader) where TCanonicalChecks : struct, IFlag
        {
            if (reader.TryReadEmptySequence()) return null;

            RlpReader fields = reader.EnterSequence<TCanonicalChecks>(out int logEntryLength);
            Rlp.GuardLimit(fields.Remaining, fields.Remaining, RlpLimit);

            Address address = fields.DecodeAddress();
            Hash256[] topics = DecodeTopics<TCanonicalChecks>(ref fields, guardLimit: true);
            byte[] data = DecodeCompactData<TCanonicalChecks>(ref fields);
            reader.ExitSequence(in fields, logEntryLength);

            return new LogEntry(address, data, topics);
        }

        public static void DecodeLogEntryStructRef(scoped ref RlpReader reader, RlpBehaviors behaviors, out LogEntryStructRef item)
        {
            if ((behaviors & RlpBehaviors.TrustedInput) != 0)
            {
                DecodeLogEntryStructRef<OffFlag>(ref reader, out item);
            }
            else
            {
                DecodeLogEntryStructRef<OnFlag>(ref reader, out item);
            }
        }

        private static void DecodeLogEntryStructRef<TCanonicalChecks>(scoped ref RlpReader reader, out LogEntryStructRef item) where TCanonicalChecks : struct, IFlag
        {
            if (reader.TryReadEmptySequence())
            {
                item = default;
                return;
            }

            RlpReader fields = reader.EnterSequence<TCanonicalChecks>(out int logEntryLength);
            Rlp.GuardLimit(fields.Remaining, fields.Remaining, RlpLimit);
            fields.DecodeAddressStructRefNonNull(out AddressStructRef address);
            ReadOnlySpan<byte> topics = fields.ReadEncodedItem<TCanonicalChecks>();
            ReadOnlySpan<byte> data = DecodeCompactData<TCanonicalChecks>(ref fields, out int zeroPrefix);
            reader.ExitSequence(in fields, logEntryLength);

            item = new LogEntryStructRef(address, data, topics, zeroPrefix);
        }

        /// <summary>Decodes the topics that <see cref="DecodeLogEntryStructRef(ref RlpReader, RlpBehaviors, out LogEntryStructRef)"/> returns as RLP.</summary>
        /// <param name="reader">A reader at the topics sequence.</param>
        /// <param name="rlpBehaviors">The behaviors of the decode; only <see cref="RlpBehaviors.TrustedInput"/> applies.</param>
        public static Hash256[] DecodeTopics(RlpReader reader, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
            => (rlpBehaviors & RlpBehaviors.TrustedInput) != 0
                ? DecodeTopics<OffFlag>(ref reader, guardLimit: false)
                : DecodeTopics<OnFlag>(ref reader, guardLimit: false);

        /// <param name="guardLimit">
        /// Whether to check the topic count of a log entry against <see cref="RlpLimit.L4"/>. The count is the topics
        /// length over the length of a whole hash, so that stored entries with more, short topics still read.
        /// </param>
        private static Hash256[] DecodeTopics<TCanonicalChecks>(ref RlpReader reader, bool guardLimit) where TCanonicalChecks : struct, IFlag
        {
            RlpReader topicsReader = reader.EnterSequence<TCanonicalChecks>(out int topicsLength);
            if (guardLimit)
            {
                Rlp.GuardLimit(topicsReader.Remaining / Rlp.LengthOfKeccakRlp, reader.Remaining, RlpLimit.L4);
            }

            // Each topic takes at least one byte, so the count cannot reach the bound.
            int topicCount = topicsReader.CountItems<TCanonicalChecks>(topicsReader.Remaining + 1);
            Hash256[] topics = topicCount == 0 ? [] : new Hash256[topicCount];
            if (topics.Length > 0)
            {
                topics[0] = topicsReader.DecodeZeroPrefixLogTopic0();
            }

            for (int i = 1; i < topics.Length; i++)
            {
                topics[i] = topicsReader.DecodeZeroPrefixKeccakNonNull();
            }

            reader.ExitSequence(in topicsReader, topicsLength);
            return topics;
        }

        /// <inheritdoc cref="IReceiptRefTopicDecoder.StartDecodingTopics"/>
        public static RlpReader StartDecodingTopics(scoped ref RlpReader reader)
        {
            RlpReader topics = reader.EnterSequence(out _);
            reader.SkipItem();
            return topics;
        }

        /// <inheritdoc cref="IReceiptRefTopicDecoder.DecodeTopic"/>
        /// <remarks>Every topic is checked as <see cref="DecodeTopics"/> checks it: an RLP null throws.</remarks>
        public static void DecodeTopic(ref RlpReader reader, scoped Span<byte> topic) => reader.DecodeZeroPrefixKeccakNonNull(topic);

        public override void Encode(ref RlpWriter writer, LogEntry? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                writer.EncodeNullObject();
                return;
            }

            (int total, int topics) = GetContentLength(item);
            RlpSequence sequence = writer.StartSequence(total);

            writer.Encode(item.Address);
            RlpSequence topicsSequence = writer.StartSequence(topics);

            for (int i = 0; i < item.Topics.Length; i++)
            {
                writer.Encode(item.Topics[i].Bytes.WithoutLeadingZerosOrEmpty());
            }

            writer.EndSequence(topicsSequence);
            ReadOnlySpan<byte> withoutLeadingZero = item.Data.WithoutLeadingZerosOrEmpty();
            int dataZeroPrefix = item.Data.Length - withoutLeadingZero.Length;
            writer.Encode(dataZeroPrefix);
            writer.Encode(withoutLeadingZero);
            writer.EndSequence(sequence);
        }

        public override int GetLength(LogEntry? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                return 1;
            }

            return Rlp.LengthOfSequence(GetContentLength(item).Total);
        }

        /// <summary>Decodes the leading-zero-stripped log data.</summary>
        private static byte[] DecodeCompactData<TCanonicalChecks>(ref RlpReader reader) where TCanonicalChecks : struct, IFlag
        {
            ReadOnlySpan<byte> rlpData = DecodeCompactData<TCanonicalChecks>(ref reader, out int zeroPrefix);
            int dataLength = zeroPrefix + rlpData.Length;
            byte[] data = dataLength == 0 ? [] : new byte[dataLength];
            rlpData.CopyTo(data.AsSpan(zeroPrefix));
            return data;
        }

        private static ReadOnlySpan<byte> DecodeCompactData<TCanonicalChecks>(scoped ref RlpReader reader, out int zeroPrefix) where TCanonicalChecks : struct, IFlag
        {
            zeroPrefix = reader.DecodePositiveInt<TCanonicalChecks>();
            ReadOnlySpan<byte> data = reader.DecodeByteArraySpan<TCanonicalChecks>();

            Rlp.GuardLimit(zeroPrefix, LogEntryDataRlpLimit.Limit - data.Length, LogEntryDataRlpLimit);

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
