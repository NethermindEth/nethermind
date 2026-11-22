// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Network;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.RLP;

namespace Nethermind.Xdc.P2P;

internal class SyncInfoMsgSerializer : IMessageSerializer<SyncInfoMsg>
{
    private static readonly SyncInfoDecoder _syncInfoDecoder = new();

    public int Serialize(Span<byte> destination, SyncInfoMsg message)
    {
        RlpWriter writer = RlpWriter.ContentWriter(destination);
        _syncInfoDecoder.Encode(ref writer, message.SyncInfo);
        return writer.Length;
    }

    public SyncInfoMsg Deserialize(ReadOnlySpan<byte> data)
    {
        RlpReader ctx = new(data);
        Types.SyncInfo syncInfo = _syncInfoDecoder.Decode(ref ctx, RlpBehaviors.None);
        ctx.CheckEnd();
        return new() { SyncInfo = syncInfo };
    }

    public int GetLength(SyncInfoMsg message) => _syncInfoDecoder.GetLength(message.SyncInfo, RlpBehaviors.None);
}
