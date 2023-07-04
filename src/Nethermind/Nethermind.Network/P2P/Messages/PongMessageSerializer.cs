// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Messages
{
    public class PongMessageSerializer : IZeroMessageSerializer<PongMessage>
    {
        public void Serialize(IByteBuffer byteBuffer, PongMessage message)
        {
            byteBuffer.WriteBytes(Rlp.OfEmptySequence.Bytes);
        }

        public PongMessage Deserialize(IByteBuffer byteBuffer)
        {
            return PongMessage.Instance;
        }
    }
}
