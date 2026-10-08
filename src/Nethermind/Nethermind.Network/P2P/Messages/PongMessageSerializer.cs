// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Messages
{
    public class PongMessageSerializer : IZeroMessageSerializer<PongMessage>
    {
        public void Serialize(Span<byte> buffer, PongMessage message)
        {
            RlpWriter writer = new(buffer);
            writer.StartSequence(0);
        }

        public int GetLength(PongMessage message, out int contentLength)
        {
            contentLength = 0;
            return Rlp.LengthOfSequence(contentLength);
        }

        public PongMessage Deserialize(ReadOnlySpan<byte> data, out int consumed)
        {
            RlpReader ctx = new(data);
            ctx.ReadSequenceLength();
            consumed = ctx.Position;
            return PongMessage.Instance;
        }
    }
}
