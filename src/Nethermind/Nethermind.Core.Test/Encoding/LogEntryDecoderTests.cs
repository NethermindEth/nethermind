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

            Assert.AreEqual(logEntry.Data, decoded.Data, "data");
            Assert.AreEqual(logEntry.LoggersAddress, decoded.LoggersAddress, "address");
            Assert.AreEqual(logEntry.Topics, decoded.Topics, "topics");
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

            KeccaksIterator iterator = new(decoded.TopicsRlp);
            for (int i = 0; i < logEntry.Topics.Length; i++)
            {
                iterator.TryGetNext(out KeccakIteratorRef keccak);
                Assert.That(logEntry.Topics[i] == keccak.Keccak, $"topics[{i}]");
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

            Assert.AreEqual(logEntry.Data, deserialized.Data, "data");
            Assert.AreEqual(logEntry.LoggersAddress, deserialized.LoggersAddress, "address");
            Assert.AreEqual(logEntry.Topics, deserialized.Topics, "topics");
        }

        [Test]
        public void Rlp_stream_and_standard_have_same_results()
        {
            LogEntry logEntry = new(TestItem.AddressA, new byte[] { 1, 2, 3 }, new[] { TestItem.KeccakA, TestItem.KeccakB });
            LogEntryDecoder decoder = LogEntryDecoder.Instance;

            Rlp rlpStreamResult = decoder.Encode(logEntry);

            Rlp rlp = decoder.Encode(logEntry);
            Assert.AreEqual(rlp.Bytes.ToHexString(), rlpStreamResult.Bytes.ToHexString());
        }
    }
}
