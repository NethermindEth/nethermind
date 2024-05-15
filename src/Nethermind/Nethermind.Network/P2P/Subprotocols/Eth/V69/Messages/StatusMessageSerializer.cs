// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;

public class StatusMessageSerializer: V62.Messages.StatusMessageSerializer,
    IZeroInnerMessageSerializer<StatusMessage>
{
    void IZeroMessageSerializer<StatusMessage>.Serialize(IByteBuffer byteBuffer, StatusMessage message)
    {
        base.Serialize(byteBuffer, message);
    }

    StatusMessage IZeroMessageSerializer<StatusMessage>.Deserialize(IByteBuffer byteBuffer)
    {
        V62.Messages.StatusMessage? message = base.Deserialize(byteBuffer);
        return new StatusMessage(message);
    }

    int IZeroInnerMessageSerializer<StatusMessage>.GetLength(StatusMessage message, out int contentLength)
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
