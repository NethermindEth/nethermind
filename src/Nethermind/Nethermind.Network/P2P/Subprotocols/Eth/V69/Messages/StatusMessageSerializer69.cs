// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;

public class StatusMessageSerializer69: V62.Messages.StatusMessageSerializer,
    IZeroInnerMessageSerializer<StatusMessage69>
{
    void IZeroMessageSerializer<StatusMessage69>.Serialize(IByteBuffer byteBuffer, StatusMessage69 message)
    {
        base.Serialize(byteBuffer, message);
    }

    StatusMessage69 IZeroMessageSerializer<StatusMessage69>.Deserialize(IByteBuffer byteBuffer)
    {
        V62.Messages.StatusMessage? message = base.Deserialize(byteBuffer);
        return new StatusMessage69(message);
    }

    int IZeroInnerMessageSerializer<StatusMessage69>.GetLength(StatusMessage69 message, out int contentLength)
    {
        return base.GetLength(message, out contentLength);
    }

    protected override RlpBehaviors GetEncodingBehavior()
    {
        return base.GetEncodingBehavior() | RlpBehaviors.Eip7642Messages;
    }

    protected override RlpBehaviors GetDecodingBehavior()
    {
        return base.GetDecodingBehavior() | RlpBehaviors.Eip7642Messages;
    }
}
