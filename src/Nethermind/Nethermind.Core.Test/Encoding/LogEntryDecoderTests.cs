// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding
{
    [TestFixture]
    public class LogEntryDecoderTests
    {
        [TestCase(true)]
        [TestCase(false)]
        public void Can_do_roundtrip(bool valueDecode)
        {
            LogEntry logEntry = new(TestItem.AddressA, new byte[] { 1, 2, 3 }, new[] { TestItem.KeccakA, TestItem.KeccakB });
            Rlp rlp = Rlp.Encode(logEntry);
            LogEntry decoded = valueDecode ? Rlp.Decode<LogEntry>(rlp.Bytes.AsSpan()) : Rlp.Decode<LogEntry>(rlp);

            Assert.That(decoded.Data, Is.EqualTo(logEntry.Data), "data");
            Assert.That(decoded.LoggersAddress, Is.EqualTo(logEntry.LoggersAddress), "address");
            Assert.That(decoded.Topics, Is.EqualTo(logEntry.Topics), "topics");
        }

        [Test]
        public void Can_do_roundtrip_ref_struct()
        {
            LogEntry logEntry = new(TestItem.AddressA, new byte[] { 1, 2, 3 }, new[] { TestItem.KeccakA, TestItem.KeccakB });
            Rlp rlp = Rlp.Encode(logEntry);
            Rlp.ValueDecoderContext valueDecoderContext = new(rlp.Bytes);
            LogEntryDecoder.DecodeStructRef(ref valueDecoderContext, RlpBehaviors.None, out LogEntryStructRef decoded);

            Assert.That(Bytes.AreEqual(logEntry.Data, decoded.Data), "data");
            Assert.That(logEntry.LoggersAddress == decoded.LoggersAddress, "address");

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
            Assert.Null(decoded);
        }

        [Test]
        public void Can_do_roundtrip_rlp_stream()
        {
            LogEntry logEntry = new(TestItem.AddressA, new byte[] { 1, 2, 3 }, new[] { TestItem.KeccakA, TestItem.KeccakB });
            LogEntryDecoder decoder = LogEntryDecoder.Instance;

            Rlp encoded = decoder.Encode(logEntry);
            LogEntry deserialized = decoder.Decode(new RlpStream(encoded.Bytes))!;

            Assert.That(deserialized.Data, Is.EqualTo(logEntry.Data), "data");
            Assert.That(deserialized.LoggersAddress, Is.EqualTo(logEntry.LoggersAddress), "address");
            Assert.That(deserialized.Topics, Is.EqualTo(logEntry.Topics), "topics");
        }

        [Test]
        public void Rlp_stream_and_standard_have_same_results()
        {
            LogEntry logEntry = new(TestItem.AddressA, new byte[] { 1, 2, 3 }, new[] { TestItem.KeccakA, TestItem.KeccakB });
            LogEntryDecoder decoder = LogEntryDecoder.Instance;

            Rlp rlpStreamResult = decoder.Encode(logEntry);

            Rlp rlp = decoder.Encode(logEntry);
            Assert.That(rlpStreamResult.Bytes.ToHexString(), Is.EqualTo(rlp.Bytes.ToHexString()));
        }
    }
}
