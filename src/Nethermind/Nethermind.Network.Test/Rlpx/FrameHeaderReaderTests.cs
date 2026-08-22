// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using DotNetty.Buffers;
using DotNetty.Codecs;
using Nethermind.Network.Rlpx;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Network.Test.Rlpx
{
    [TestFixture]
    public class FrameHeaderReaderTests
    {
        [Test]
        [TestCaseSource(nameof(TotalPacketSizeExceedsLimitValidCases))]
        [TestCaseSource(nameof(TotalPacketSizeExceedsLimitInvalidCases))]
        public bool Throws_when_total_packet_size_exceeds_limit(int frameSize, long totalPacketSize)
        {
            FrameHeaderReader reader = new();
            using DisposableByteBuffer buffer = Unpooled.Buffer(Frame.HeaderSize).AsDisposable();

            try
            {
                buffer.WriteByte(frameSize >> 16);
                buffer.WriteByte(frameSize >> 8);
                buffer.WriteByte(frameSize);

                int contentLength = Rlp.LengthOf(0) + Rlp.LengthOf(1) + Rlp.LengthOf(totalPacketSize);
                buffer.EnsureWritable(Rlp.LengthOfSequence(contentLength));
                ByteBufferRlpWriter writer = new(buffer);
                writer.StartSequence(contentLength);
                writer.Encode(0);
                writer.Encode(1);
                writer.Encode(totalPacketSize);

                buffer.WriteZero(Frame.HeaderSize - buffer.WriterIndex);

                reader.ReadFrameHeader(buffer);
            }
            catch (CorruptedFrameException)
            {
                return false;
            }

            return true;
        }

        private static IEnumerable<TestCaseData> TotalPacketSizeExceedsLimitValidCases()
        {
            yield return new(32, 64) { TestName = "A normal packet", ExpectedResult = true };
            yield return new(1, SnappyParameters.MaxSnappyLength) { TestName = "Total_size_is_exactly_snappy_limit", ExpectedResult = true };
        }

        private static IEnumerable<TestCaseData> TotalPacketSizeExceedsLimitInvalidCases()
        {
            yield return new(1, (long)(SnappyParameters.MaxSnappyLength + 1)) { TestName = "Total_size_exceeds_snappy_limit_small_frame", ExpectedResult = false };
            yield return new(128, (long)(SnappyParameters.MaxSnappyLength + 256)) { TestName = "Total_size_exceeds_snappy_limit_mid_frame", ExpectedResult = false };
            yield return new(Frame.HeaderSize, (long)(int.MaxValue)) { TestName = "Total_size_exceeds_snappy_limit_max_value", ExpectedResult = false };
            yield return new(200, 100L) { TestName = "Frame_size_cannot_exceed_total_size", ExpectedResult = false };
            yield return new(1, (long)uint.MaxValue) { TestName = "Total_size_cannot_be_negative", ExpectedResult = false };
        }

        [Test]
        [TestCaseSource(nameof(MalformedHeaderBodyPrefixes))]
        public void Throws_corrupted_frame_when_rlp_header_body_is_malformed(byte[] headerBodyPrefix)
        {
            FrameHeaderReader reader = new();
            using DisposableByteBuffer buffer = Unpooled.Buffer(Frame.HeaderSize).AsDisposable();

            const int frameSize = 1;
            buffer.WriteByte(frameSize >> 16);
            buffer.WriteByte(frameSize >> 8);
            buffer.WriteByte(frameSize);

            buffer.WriteBytes(headerBodyPrefix);
            buffer.WriteZero(Frame.HeaderSize - buffer.WriterIndex);

            Assert.That(() => reader.ReadFrameHeader(buffer),
                Throws.InstanceOf<CorruptedFrameException>(),
                "malformed header body RLP must be rejected as corrupted frame");
        }

        private static IEnumerable<TestCaseData> MalformedHeaderBodyPrefixes()
        {
            yield return new TestCaseData(new byte[] { 0xf7 }).SetName("Short_list_length_exceeds_available_header_body");
            yield return new TestCaseData(new byte[] { 0xf8, 0x00 }).SetName("Long_list_uses_non_canonical_zero_length");
            yield return new TestCaseData(new byte[] { 0xf8, 0x38 }).SetName("Long_list_length_exceeds_available_header_body");
            yield return new TestCaseData(new byte[] { 0xfb, 0xff, 0xff, 0xff, 0xff }).SetName("Long_list_length_overflows_int");
            yield return new TestCaseData(new byte[] { 0xff }).SetName("Long_list_length_of_length_exceeds_supported_width");
        }
    }
}
