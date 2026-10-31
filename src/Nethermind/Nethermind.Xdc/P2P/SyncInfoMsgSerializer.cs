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

    public void Serialize(IByteBuffer byteBuffer, SyncInfoMsg message) =>
        byteBuffer.WriteRlp(_syncInfoDecoder, message.SyncInfo);

    public SyncInfoMsg Deserialize(IByteBuffer byteBuffer)
    {
        Types.SyncInfo syncInfo = byteBuffer.DeserializeRlp(_syncInfoDecoder);
        return new() { SyncInfo = syncInfo };
    }

    public int GetLength(SyncInfoMsg message, out int contentLength)
    {
        contentLength = _syncInfoDecoder.GetContentLength(message.SyncInfo, RlpBehaviors.None);
        return _syncInfoDecoder.GetLength(message.SyncInfo, RlpBehaviors.None);
    }
}
