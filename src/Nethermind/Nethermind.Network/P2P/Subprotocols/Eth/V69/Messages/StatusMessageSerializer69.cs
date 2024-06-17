// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;

public class StatusMessageSerializer69 :
    V62.Messages.StatusMessageSerializer,
    IZeroInnerMessageSerializer<StatusMessage69>
{
    public StatusMessageSerializer69() : base(includeTd: false) { }

    void IZeroMessageSerializer<StatusMessage69>.Serialize(IByteBuffer byteBuffer, StatusMessage69 message) => base.Serialize(byteBuffer, message);

    StatusMessage69 IZeroMessageSerializer<StatusMessage69>.Deserialize(IByteBuffer byteBuffer)
    {
        V62.Messages.StatusMessage? message = base.Deserialize(byteBuffer);
        return new(message);
    }

    int IZeroInnerMessageSerializer<StatusMessage69>.GetLength(StatusMessage69 message, out int contentLength)
    {
        return base.GetLength(message, out contentLength);
    }
}
