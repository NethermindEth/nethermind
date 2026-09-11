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
        /// <param name="ctx">Reader positioned at the first item of the log sequence.</param>
        /// <param name="logsEnd">Position one past the last byte of the log sequence.</param>
        /// <exception cref="RlpLimitException">The declared count exceeds what the bytes or the gas ceiling allow.</exception>
        public static LogEntry[] DecodeLogs(ref RlpReader ctx, int logsEnd)
        {
            RlpLimit logsRlpLimit = RlpLimit.ReceiptLogs;
            int maxLogs = Math.Min(logsRlpLimit.Limit, (logsEnd - ctx.Position) / MinNonNullEncodedLength);
            int logCount = ctx.PeekNumberOfItemsRemaining(logsEnd, maxLogs + 1);
            Rlp.GuardLimit(logCount, maxLogs, logsRlpLimit);

            LogEntry[] logs = new LogEntry[logCount];
            for (int i = 0; i < logCount; i++)
            {
                logs[i] = Instance.DecodeGuardNotNull(ref ctx, RlpBehaviors.AllowExtraBytes);
            }

            return logs;
        }

        protected override LogEntry? DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (decoderContext.TryConsumeNull(out LiteRlpReader rlp, out int position)) return null;

            rlp.ReadSequenceLength(ref position, out int logEntryLength);
            Rlp.GuardLimit(logEntryLength, rlp.Data.Length - position, RlpLimit);
            int logEntryCheck = position + logEntryLength;

            rlp.DecodeAddress(ref position, out Address address);
            rlp.ReadSequenceLength(ref position, out int topicsLength);
            int topicsCheck = position + topicsLength;
            int topicCount = topicsLength / Rlp.LengthOfKeccakRlp;
            Rlp.GuardLimit(topicCount, rlp.Data.Length - position, RlpLimit.L4);

            Hash256[] topics = new Hash256[topicCount];
            for (int i = 0; i < topics.Length; i++)
            {
                rlp.DecodeKeccak(ref position, out topics[i]);
            }

            RlpHelpers.Check(position, topicsCheck);
            rlp.DecodeByteArray(ref position, out byte[] data);
            RlpHelpers.Check(position, logEntryCheck);
            decoderContext.Position = position;

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
                item = default;
                return;
            }

            int logEntryLength = decoderContext.ReadSequenceLength();
            int logEntryCheck = decoderContext.Position + logEntryLength;
            decoderContext.DecodeAddressStructRefNonNull(out AddressStructRef address);
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
