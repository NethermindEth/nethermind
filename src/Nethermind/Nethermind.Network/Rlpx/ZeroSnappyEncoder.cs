// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using DotNetty.Buffers;
using DotNetty.Codecs;
using DotNetty.Transport.Channels;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Snappier;

namespace Nethermind.Network.Rlpx;

public class ZeroSnappyEncoder(ILogManager logManager) : MessageToByteEncoder<IByteBuffer>
{
    private readonly ILogger _logger = logManager?.GetClassLogger<ZeroSnappyEncoder>() ?? throw new ArgumentNullException(nameof(logManager));

    protected override void Encode(IChannelHandlerContext context, IByteBuffer input, IByteBuffer output)
    {
        Rlp.ValueDecoderContext decoderContext = new(input.AsSpan());
        int packetLength = decoderContext.PeekNextRlpLength();

        int maxLength = Snappy.GetMaxCompressedLength(input.ReadableBytes);
        output.EnsureWritable(packetLength + maxLength);
        output.WriteBytes(input.ReadBytes(packetLength));

        if (_logger.IsTrace) _logger.Trace($"Compressing with Snappy a message of length {input.ReadableBytes}");

        int length = Snappy.Compress(
            input.Array.AsSpan(input.ArrayOffset + input.ReaderIndex, input.ReadableBytes),
            output.Array.AsSpan(output.ArrayOffset + output.WriterIndex, maxLength));

        input.SetReaderIndex(input.ReaderIndex + input.ReadableBytes);
        output.SetWriterIndex(output.WriterIndex + length);
    }
}
