// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using DotNetty.Buffers;
using DotNetty.Codecs;
using DotNetty.Transport.Channels;

namespace Nethermind.Network.Rlpx;

public class OneTimeLengthFieldBasedFrameDecoder : LengthFieldBasedFrameDecoder
{
    private bool _decoded;

    public OneTimeLengthFieldBasedFrameDecoder() : base(ByteOrder.BigEndian, ushort.MaxValue, 0, 2, 0, 0, true)
    {
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected override void Decode(IChannelHandlerContext context, IByteBuffer input, List<object> output)
    {
        if (_decoded) return;
        base.Decode(context, input, output);

        // Base class decode one at a time only
        _decoded = output.Count > 0;
    }
}
