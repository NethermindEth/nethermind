// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Caching;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding;

public class LogEntryDecoderTests
{
    private static LogEntry CreateSampleLogEntry() =>
        new(TestItem.AddressA, new byte[] { 1, 2, 3 }, new[] { TestItem.KeccakA, TestItem.KeccakB });

    [TestCase(true, false)]
    [TestCase(false, false)]
    [TestCase(false, true)]
    public void Can_do_roundtrip(bool valueDecode, bool useDecoderInstance)
    {
        LogEntry logEntry = CreateSampleLogEntry();
        LogEntryDecoder decoder = LogEntryDecoder.Instance;

        Rlp rlp = useDecoderInstance
            ? decoder.Encode(logEntry)
            : Rlp.Encode(logEntry);

        LogEntry? decoded;
        if (useDecoderInstance)
        {
            RlpReader ctx = new(rlp.Bytes);
            decoded = decoder.Decode(ref ctx);
        }
        else
        {
            decoded = valueDecode
                ? Rlp.Decode<LogEntry?>(rlp.Bytes.AsSpan())
                : Rlp.Decode<LogEntry?>(rlp);
        }

        Assert.That(decoded, Is.EqualTo(logEntry).UsingPropertiesComparer());
    }

    [Test]
    public void Can_do_roundtrip_ref_struct()
    {
        LogEntry logEntry = CreateSampleLogEntry();
        Rlp rlp = Rlp.Encode(logEntry);
        RlpReader reader = new(rlp.Bytes);
        LogEntryDecoder.DecodeStructRef(ref reader, RlpBehaviors.None, out LogEntryStructRef decoded);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Bytes.AreEqual(logEntry.Data, decoded.Data), "data");
            Assert.That(logEntry.Address == decoded.Address, "address");
        }

        Span<byte> buffer = stackalloc byte[32];
        KeccaksIterator iterator = new(decoded.TopicsRlp, buffer);
        for (int i = 0; i < logEntry.Topics.Length; i++)
        {
            iterator.TryGetNext(out Hash256StructRef keccak);
            Assert.That(logEntry.Topics[i] == keccak, $"topics[{i}]");
        }
    }

    public enum TopicDecodePath
    {
        Full,
        Compact,
        CompactTopicsOnly,
    }

    [Test, NonParallelizable]
    public void Decoded_logs_share_the_topic_0_instance_only([Values] TopicDecodePath path)
    {
        LogEntry logEntry = new(TestItem.AddressA, [1, 2, 3], [Keccak.Compute(nameof(Decoded_logs_share_the_topic_0_instance_only)), TestItem.KeccakB]);

        Hash256[] first = DecodeTopics(logEntry, path);
        Hash256[] second = DecodeTopics(logEntry, path);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Is.EqualTo(logEntry.Topics));
            Assert.That(second, Is.EqualTo(logEntry.Topics));
            Assert.That(second[0], Is.SameAs(first[0]));
            Assert.That(second[1], Is.Not.SameAs(first[1]));
        }
    }

    [Test, NonParallelizable]
    public void Topic_0_values_sharing_a_cache_slot_decode_to_their_own_value([Values] TopicDecodePath path)
    {
        (byte[] a, byte[] b) = LogTopicCacheTests.CollidingPair();
        LogEntry logA = new(TestItem.AddressA, [], [new Hash256(a), TestItem.KeccakB]);
        LogEntry logB = new(TestItem.AddressA, [], [new Hash256(b), TestItem.KeccakB]);

        Hash256[] fromA = DecodeTopics(logA, path);
        Hash256[] fromB = DecodeTopics(logB, path);
        Hash256[] fromAAgain = DecodeTopics(logA, path);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fromA, Is.EqualTo(logA.Topics));
            Assert.That(fromB, Is.EqualTo(logB.Topics));
            Assert.That(fromAAgain, Is.EqualTo(logA.Topics));
        }
    }

    private static Hash256[] DecodeTopics(LogEntry logEntry, TopicDecodePath path)
    {
        if (path == TopicDecodePath.Full)
        {
            RlpReader ctx = new(LogEntryDecoder.Instance.Encode(logEntry).Bytes);
            return LogEntryDecoder.Instance.Decode(ref ctx)!.Topics;
        }

        RlpReader reader = new(CompactLogEntryDecoder.Instance.Encode(logEntry).Bytes);
        if (path == TopicDecodePath.Compact)
        {
            return CompactLogEntryDecoder.Instance.Decode(ref reader)!.Topics;
        }

        CompactLogEntryDecoder.DecodeLogEntryStructRef(ref reader, RlpBehaviors.None, out LogEntryStructRef structRef);
        return CompactLogEntryDecoder.DecodeTopics(new RlpReader(structRef.TopicsRlp));
    }

    [Test]
    public void Can_handle_nulls()
    {
        Rlp rlp = Rlp.Encode((LogEntry)null!);
        LogEntry? decoded = Rlp.Decode<LogEntry?>(rlp);
        Assert.That(decoded, Is.Null);
    }

    [Test]
    public void Interface_decoders_return_null_for_empty_log_entry([Values] bool compact)
    {
        RlpDecoder<LogEntry?> decoder = compact ? CompactLogEntryDecoder.Instance : LogEntryDecoder.Instance;
        RlpReader ctx = new(Rlp.OfEmptyList.Bytes);

        Assert.That(decoder.Decode(ref ctx), Is.Null);
    }

    [Test]
    public void Storage_struct_ref_decoders_return_default_for_empty_log_entry([Values] bool compact)
    {
        RlpReader reader = new(Rlp.OfEmptyList.Bytes);

        if (compact)
        {
            CompactLogEntryDecoder.DecodeLogEntryStructRef(ref reader, RlpBehaviors.None, out LogEntryStructRef logEntry);
            AssertDefault(logEntry);
        }
        else
        {
            LogEntryDecoder.DecodeStructRef(ref reader, RlpBehaviors.None, out LogEntryStructRef logEntry);
            AssertDefault(logEntry);
        }

        static void AssertDefault(LogEntryStructRef logEntry)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(logEntry.Address.Bytes.Length, Is.Zero);
                Assert.That(logEntry.Data.Length, Is.Zero);
                Assert.That(logEntry.TopicsRlp.Length, Is.Zero);
            }
        }
    }

    [Test]
    public void Struct_ref_decoders_reject_null_address([Values] bool compact)
    {
        Rlp malformed = compact
            ? Rlp.Encode(Rlp.OfEmptyByteArray, Rlp.OfEmptyList, Rlp.Encode(0), Rlp.OfEmptyByteArray)
            : Rlp.Encode(Rlp.OfEmptyByteArray, Rlp.OfEmptyList, Rlp.OfEmptyByteArray);

        Assert.That(Decode, Throws.TypeOf<RlpException>());

        void Decode()
        {
            RlpReader reader = new(malformed.Bytes);
            if (compact)
            {
                CompactLogEntryDecoder.DecodeLogEntryStructRef(ref reader, RlpBehaviors.None, out _);
            }
            else
            {
                LogEntryDecoder.DecodeStructRef(ref reader, RlpBehaviors.None, out _);
            }
        }
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
            RlpReader ctx = new(malformed.Bytes);
            LogEntryDecoder.Instance.Decode(ref ctx);
        });
    }

    [Test]
    public void Compact_decoder_rejects_zero_prefix_that_expands_data_beyond_limit([Values] bool useStructRef)
    {
        Rlp malformed = CreateCompactLogEntryWithTooLargeZeroPrefix();

        Assert.Throws<RlpLimitException>(() =>
        {
            RlpReader ctx = new(malformed.Bytes);
            if (useStructRef)
            {
                CompactLogEntryDecoder.DecodeLogEntryStructRef(ref ctx, RlpBehaviors.None, out _);
            }
            else
            {
                CompactLogEntryDecoder.Instance.Decode(ref ctx);
            }
        });
    }

    [Test]
    public void Compact_struct_ref_decoder_rejects_log_entry_length_beyond_limit()
    {
        byte[] malformed = CreateCompactLogEntryWithTooLargeDeclaredLength();

        Assert.Throws<RlpLimitException>(() =>
        {
            RlpReader ctx = new(malformed);
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
