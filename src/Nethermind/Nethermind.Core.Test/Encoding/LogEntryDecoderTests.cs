// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding;

public class LogEntryDecoderTests
{
    private static LogEntry CreateSampleLogEntry() =>
        new(TestItem.AddressA, new byte[] { 1, 2, 3 }, new[] { TestItem.KeccakA, TestItem.KeccakB });

    private static void AssertLogEntriesEqual(LogEntry expected, LogEntry actual)
    {
        Assert.That(actual.Data, Is.EqualTo(expected.Data), "data");
        Assert.That(actual.Address, Is.EqualTo(expected.Address), "address");
        Assert.That(actual.Topics, Is.EqualTo(expected.Topics), "topics");
    }

    [TestCase(true, false)]
    [TestCase(false, false)]
    [TestCase(false, true)]
    public void Can_do_roundtrip(bool valueDecode, bool useDecoderInstance)
    {
        LogEntry logEntry = CreateSampleLogEntry();

        Rlp rlp = useDecoderInstance
            ? LogEntryDecoder.Instance.Encode(logEntry)
            : Rlp.Encode(logEntry);

        LogEntry decoded;
        if (useDecoderInstance)
        {
            Rlp.ValueDecoderContext ctx = new(rlp.Bytes);
            decoded = LogEntryDecoder.Instance.Decode(ref ctx)!;
        }
        else
        {
            decoded = valueDecode
                ? Rlp.Decode<LogEntry>(rlp.Bytes.AsSpan())
                : Rlp.Decode<LogEntry>(rlp);
        }

        AssertLogEntriesEqual(logEntry, decoded);
    }

    [Test]
    public void Can_do_roundtrip_ref_struct()
    {
        LogEntry logEntry = CreateSampleLogEntry();
        Rlp rlp = Rlp.Encode(logEntry);
        Rlp.ValueDecoderContext valueDecoderContext = new(rlp.Bytes);
        LogEntryDecoder.DecodeStructRef(ref valueDecoderContext, RlpBehaviors.None, out LogEntryStructRef decoded);

        Assert.That(Bytes.AreEqual(logEntry.Data, decoded.Data), "data");
        Assert.That(logEntry.Address == decoded.Address, "address");

        Span<byte> buffer = stackalloc byte[32];
        KeccaksIterator iterator = new(decoded.TopicsRlp, buffer);
        for (int i = 0; i < logEntry.Topics.Length; i++)
        {
            iterator.TryGetNext(out Hash256StructRef keccak);
            Assert.That(logEntry.Topics[i] == keccak, $"topics[{i}]");
        }
    }

    [Test]
    public void Can_handle_nulls()
    {
        Rlp rlp = Rlp.Encode((LogEntry)null!);
        LogEntry decoded = Rlp.Decode<LogEntry>(rlp);
        Assert.That(decoded, Is.Null);
    }

    [Test]
    public void Rejects_extra_topic_items_inside_topics_sequence()
    {
        Rlp malformed = Rlp.Encode(
            Rlp.Encode(TestItem.AddressA.Bytes),
            Rlp.Encode(Rlp.Encode(TestItem.KeccakA.Bytes), Rlp.OfEmptyByteArray),
            Rlp.OfEmptyByteArray);

        Assert.Throws<RlpException>(() =>
        {
            Rlp.ValueDecoderContext ctx = new(malformed.Bytes);
            LogEntryDecoder.Instance.Decode(ref ctx);
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Compact_decoder_rejects_zero_prefix_that_expands_data_beyond_limit(bool useStructRef)
    {
        Rlp malformed = CreateCompactLogEntryWithTooLargeZeroPrefix();

        Assert.Throws<RlpLimitException>(() =>
        {
            Rlp.ValueDecoderContext ctx = new(malformed.Bytes);
            if (useStructRef)
            {
                CompactLogEntryDecoder.DecodeLogEntryStructRef(ref ctx, RlpBehaviors.None, out _);
            }
            else
            {
                CompactLogEntryDecoder.Decode(ref ctx);
            }
        });
    }

    [Test]
    public void Compact_struct_ref_decoder_rejects_log_entry_length_beyond_limit()
    {
        byte[] malformed = CreateCompactLogEntryWithTooLargeDeclaredLength();

        Assert.Throws<RlpLimitException>(() =>
        {
            Rlp.ValueDecoderContext ctx = new(malformed);
            CompactLogEntryDecoder.DecodeLogEntryStructRef(ref ctx, RlpBehaviors.None, out _);
        });
    }

    // This simulates a malformed compact log entry wire payload: [address, topics, zeroPrefix, rlpData].
    private static Rlp CreateCompactLogEntryWithTooLargeZeroPrefix() => Rlp.Encode(
        Rlp.Encode(TestItem.AddressA.Bytes),
        Rlp.OfEmptyList,
        Rlp.Encode((int)16.MB),
        Rlp.Encode(new byte[] { 1 }));

    private static byte[] CreateCompactLogEntryWithTooLargeDeclaredLength()
    {
        int declaredLength = (int)16.MB + 1;
        return
        [
            0xfa,
            (byte)(declaredLength >> 16),
            (byte)(declaredLength >> 8),
            (byte)declaredLength,
        ];
    }
}
