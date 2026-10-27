// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Network;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.RLP;

namespace Nethermind.Xdc.P2P;

internal class SyncInfoMsgSerializer : IZeroInnerMessageSerializer<SyncInfoMsg>
{
    private static readonly SyncInfoDecoder _syncInfoDecoder = new();

    public void Serialize(IByteBuffer byteBuffer, SyncInfoMsg message) => this.SerializeWithLengths(byteBuffer, message);

    public void Serialize(IByteBuffer byteBuffer, SyncInfoMsg message, ref LengthStack lengths)
    {
        ByteBufferRlpWriter writer = new(byteBuffer);
        _syncInfoDecoder.Encode(ref writer, message.SyncInfo, ref lengths);
    }

    public SyncInfoMsg Deserialize(IByteBuffer byteBuffer)
    {
        RlpReader ctx = new(byteBuffer.AsSpan());
        Types.SyncInfo syncInfo = _syncInfoDecoder.Decode(ref ctx, RlpBehaviors.None);
        byteBuffer.SkipBytes(ctx.Position);
        return new() { SyncInfo = syncInfo };
    }

    public void PopulateLength(SyncInfoMsg message, ref LengthStack lengths) =>
        _syncInfoDecoder.PopulateLength(message.SyncInfo, ref lengths);
}
