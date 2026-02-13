// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Network;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.SyncLimits;
using System;
using System.Collections.Generic;
using System.Text;

namespace Nethermind.Xdc.P2P;

internal class SyncinfoMsgSerializer : IZeroInnerMessageSerializer<SyncInfoMsg>
{
    private static readonly SyncInfoDecoder _syncInfoDecoder = new SyncInfoDecoder();

    public void Serialize(IByteBuffer byteBuffer, SyncInfoMsg message)
    {
        int totalLength = GetLength(message, out int contentLength);
        byteBuffer.EnsureWritable(totalLength);
        NettyRlpStream stream = new(byteBuffer);
        _syncInfoDecoder.Encode(stream, message.SyncInfo);
    }

    public SyncInfoMsg Deserialize(IByteBuffer byteBuffer)
    {
        Memory<byte> memory = byteBuffer.AsMemory();
        Rlp.ValueDecoderContext ctx = new(memory, true);
        Types.SyncInfo syncInfo = _syncInfoDecoder.Decode(ref ctx, RlpBehaviors.None);
        byteBuffer.SkipBytes(memory.Length);
        return new() { SyncInfo = syncInfo };
    }

    public int GetLength(SyncInfoMsg message, out int contentLength)
    {
        contentLength = _syncInfoDecoder.GetContentLength(message.SyncInfo, RlpBehaviors.None);
        return _syncInfoDecoder.GetLength(message.SyncInfo, RlpBehaviors.None);
    }
}
