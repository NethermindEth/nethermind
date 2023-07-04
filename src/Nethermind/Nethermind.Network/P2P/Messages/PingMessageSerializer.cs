// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Messages
{
    public class PingMessageSerializer : IZeroMessageSerializer<PingMessage>
    {
        public void Serialize(IByteBuffer byteBuffer, PingMessage message)
        {
            byteBuffer.WriteBytes(Rlp.OfEmptySequence.Bytes);
        }

        public PingMessage Deserialize(IByteBuffer byteBuffer)
        {
            return PingMessage.Instance;
        }
    }
}
