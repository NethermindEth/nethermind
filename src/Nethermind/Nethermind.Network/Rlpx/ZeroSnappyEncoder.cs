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

/// <summary>
/// Compresses messages for the split-pipeline test oracle.
/// </summary>
/// <remarks>Production channels use the combined <see cref="ZeroPacketSplitter"/> encoder.</remarks>
public class ZeroSnappyEncoder(ILogManager logManager) : MessageToByteEncoder<IByteBuffer>
{
    private readonly ILogger _logger = logManager?.GetClassLogger<ZeroSnappyEncoder>() ?? throw new ArgumentNullException(nameof(logManager));

    protected override void Encode(IChannelHandlerContext context, IByteBuffer input, IByteBuffer output)
    {
        ReadOnlySpan<byte> packet = input.AsSpan();
        // Covers the packet type, which is copied as is, and the compressed content.
        output.EnsureWritable(Snappy.GetMaxCompressedLength(packet.Length));
        int written = FrameEncoder.Compress(packet, output.Array.AsSpan(output.ArrayOffset + output.WriterIndex, output.WritableBytes), _logger);
        input.SkipBytes(packet.Length);
        output.SetWriterIndex(output.WriterIndex + written);
    }
}
