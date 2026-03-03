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
            IByteBuffer buffer = Unpooled.Buffer(Frame.HeaderSize);

            try
            {
                buffer.WriteByte(frameSize >> 16);
                buffer.WriteByte(frameSize >> 8);
                buffer.WriteByte(frameSize);

                NettyRlpStream stream = new(buffer);
                int contentLength = Rlp.LengthOf(0) + Rlp.LengthOf(1) + Rlp.LengthOf(totalPacketSize);
                buffer.EnsureWritable(Rlp.LengthOfSequence(contentLength));
                stream.StartSequence(contentLength);
                stream.Encode(0);
                stream.Encode(1);
                stream.Encode(totalPacketSize);

                buffer.WriteZero(Frame.HeaderSize - buffer.WriterIndex);

                reader.ReadFrameHeader(buffer);
            }
            catch (CorruptedFrameException)
            {
                return false;
            }
            finally
            {
                buffer.Release();
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
    }
}
