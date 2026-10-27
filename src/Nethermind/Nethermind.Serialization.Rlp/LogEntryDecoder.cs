// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
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
        /// <param name="ctx">Reader positioned at the first item of the log sequence.</param>
        /// <param name="logsEnd">Position one past the last byte of the log sequence.</param>
        /// <exception cref="RlpLimitException">The declared count exceeds what the bytes or the gas ceiling allow.</exception>
        public static LogEntry[] DecodeLogs(ref RlpReader ctx, int logsEnd)
        {
            RlpLimit logsRlpLimit = RlpLimit.ReceiptLogs;
            int maxLogs = Math.Min(logsRlpLimit.Limit, (logsEnd - ctx.Position) / MinNonNullEncodedLength);
            int logCount = ctx.PeekNumberOfItemsRemaining(logsEnd, maxLogs + 1);
            Rlp.GuardLimit(logCount, maxLogs, logsRlpLimit);

            LogEntry[] logs = logCount == 0 ? [] : new LogEntry[logCount];
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
            rlp = new(rlp.Data[..logEntryCheck]);

            rlp.DecodeAddress(ref position, out Address address);
            rlp.ReadSequenceLength(ref position, out int topicsLength);
            int topicsCheck = position + topicsLength;
            int topicCount = topicsLength / Rlp.LengthOfKeccakRlp;
            Rlp.GuardLimit(topicCount, rlp.Data.Length - position, RlpLimit.L4);

            Hash256[] topics = topicCount == 0 ? [] : new Hash256[topicCount];
            if (topics.Length > 0)
            {
                rlp.DecodeLogTopic0(ref position, out topics[0]);
            }

            for (int i = 1; i < topics.Length; i++)
            {
                rlp.DecodeKeccak(ref position, out topics[i]);
            }

            RlpHelpers.Check(position, topicsCheck);
            rlp.DecodeByteArray(ref position, out byte[] data);
            RlpHelpers.Check(position, logEntryCheck);
            decoderContext.Position = position;

            return new LogEntry(address, data, topics);
        }

        public override void Encode<TWriter>(ref TWriter writer, LogEntry? item, ref LengthStack lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                writer.EncodeNullObject();
                return;
            }

            writer.StartSequence(lengths.Pop());

            writer.Encode(item.Address);
            writer.StartSequence(GetTopicsLength(item));

            for (int i = 0; i < item.Topics.Length; i++)
            {
                writer.Encode(item.Topics[i]);
            }

            writer.Encode(item.Data);
        }

        public override int PopulateLength(LogEntry? item, ref LengthStack lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                return 1;
            }

            int contentLength = Rlp.LengthOf(item.Address)
                + Rlp.LengthOfSequence(GetTopicsLength(item))
                + Rlp.LengthOf(item.Data);
            lengths.Push(contentLength);
            return Rlp.LengthOfSequence(contentLength);
        }

        // Every topic is a 32-byte word, as the decoder's topic count also assumes.
        private static int GetTopicsLength(LogEntry item)
        {
            Debug.Assert(Array.TrueForAll(item.Topics, static topic => topic is not null), "A null topic encodes shorter than the length counted for it.");
            return item.Topics.Length * Rlp.LengthOfKeccakRlp;
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
