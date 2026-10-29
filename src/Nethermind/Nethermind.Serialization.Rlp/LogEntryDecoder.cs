// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.FluentRlp;
using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Serialization.Rlp
{
    [method: DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(LogEntryDecoder))]
    public sealed class LogEntryDecoder() : RlpDecoder<LogEntry?>
    {
        private static readonly RlpLimit RlpLimit = RlpLimit.For<LogEntry>((int)16.MB, nameof(LogEntry));

        /// <summary>Smallest RLP encoding of a non-null log entry.</summary>
        /// <remarks>
        /// <c>[address, [], ""]</c> - a 21-byte address, an empty topics list and empty data under a
        /// one-byte sequence prefix. Only a valid divisor for readers that reject nulls, because the
        /// decoder accepts a one-byte <c>0xC0</c> as a null - hence <see cref="DecodeLogs"/> owning it.
        /// </remarks>
        internal const int MinNonNullEncodedLength = 24;

        public static LogEntryDecoder Instance { get; } = new();

        /// <summary>Decodes a receipt's log sequence, rejecting a count the sequence's own bytes cannot back.</summary>
        /// <remarks>
        /// Nulls are rejected here rather than by the caller, which is what makes
        /// <see cref="MinNonNullEncodedLength"/> a sound bound on the count before the array is
        /// allocated. Counting stops at the tighter of the two bounds so a message that will be
        /// rejected is not scanned to its end.
        /// </remarks>
        /// <param name="logs">Reader over the payload of the log sequence; the caller ends the sequence.</param>
        /// <exception cref="RlpLimitException">The declared count exceeds what the bytes or the gas ceiling allow.</exception>
        public static LogEntry[] DecodeLogs(ref RlpReader logs)
        {
            RlpLimit logsRlpLimit = RlpLimit.ReceiptLogs;
            int maxLogs = Math.Min(logsRlpLimit.Limit, logs.Remaining / MinNonNullEncodedLength);
            int logCount = logs.CountItems(maxLogs, logsRlpLimit);

            LogEntry[] result = logCount == 0 ? [] : new LogEntry[logCount];
            for (int i = 0; i < logCount; i++)
            {
                result[i] = Instance.DecodeGuardNotNull(ref logs);
            }

            return result;
        }

        protected override LogEntry? DecodeInternal(ref RlpReader reader, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (reader.TryReadEmptySequence()) return null;

            RlpReader fields = reader.EnterSequence(out int logEntryLength);
            Rlp.GuardLimit(fields.Remaining, fields.Remaining, RlpLimit);

            Address address = fields.DecodeAddress();
            RlpReader topicsReader = fields.EnterSequence(out int topicsLength);
            int topicCount = topicsReader.Remaining / Rlp.LengthOfKeccakRlp;
            Rlp.GuardLimit(topicCount, topicsReader.Remaining, RlpLimit.L4);

            Hash256[] topics = topicCount == 0 ? [] : new Hash256[topicCount];
            if (topics.Length > 0)
            {
                topics[0] = topicsReader.DecodeLogTopic0();
            }

            for (int i = 1; i < topics.Length; i++)
            {
                topics[i] = topicsReader.DecodeKeccak();
            }

            fields.ExitSequence(in topicsReader, topicsLength);
            byte[] data = fields.DecodeByteArray();
            reader.ExitSequence(in fields, logEntryLength);

            return new LogEntry(address, data, topics);
        }

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
                writer.Encode(item.Topics[i]);
            }

            writer.EndSequence(topicsSequence);
            writer.Encode(item.Data);
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

        // Every topic is a 32-byte word, as the decoder's topic count also assumes.
        private static int GetTopicsLength(LogEntry item)
        {
            Debug.Assert(Array.TrueForAll(item.Topics, static topic => topic is not null), "A null topic encodes shorter than the length counted for it.");
            return item.Topics.Length * Rlp.LengthOfKeccakRlp;
        }

        public static void DecodeStructRef(scoped ref RlpReader reader, RlpBehaviors storage, out LogEntryStructRef item)
        {
            if (reader.TryReadEmptySequence())
            {
                item = default;
                return;
            }

            RlpReader fields = reader.EnterSequence(out int logEntryLength);
            fields.DecodeAddressStructRefNonNull(out AddressStructRef address);
            ReadOnlySpan<byte> topics = fields.ReadEncodedItem();
            ReadOnlySpan<byte> data = fields.DecodeByteArraySpan();
            reader.ExitSequence(in fields, logEntryLength);

            item = new LogEntryStructRef(address, data, topics);
        }
    }
}
