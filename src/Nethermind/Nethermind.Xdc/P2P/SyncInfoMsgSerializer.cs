// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Network;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.RLP;

namespace Nethermind.Xdc.P2P;

internal class SyncInfoMsgSerializer : IZeroMessageSerializer<SyncInfoMsg>
{
    private static readonly SyncInfoDecoder _syncInfoDecoder = new();

    public void Serialize(Span<byte> buffer, SyncInfoMsg message)
    {
        RlpWriter writer = new(buffer);
        _syncInfoDecoder.Encode(ref writer, message.SyncInfo);
    }

    public SyncInfoMsg Deserialize(ReadOnlySpan<byte> data, out int consumed)
    {
        RlpReader ctx = new(data);
        Types.SyncInfo syncInfo = _syncInfoDecoder.Decode(ref ctx, RlpBehaviors.None);
        consumed = ctx.Position;
        return new() { SyncInfo = syncInfo };
    }

    public int GetLength(SyncInfoMsg message, out int contentLength)
    {
        contentLength = _syncInfoDecoder.GetContentLength(message.SyncInfo, RlpBehaviors.None);
        return _syncInfoDecoder.GetLength(message.SyncInfo, RlpBehaviors.None);
    }
}
