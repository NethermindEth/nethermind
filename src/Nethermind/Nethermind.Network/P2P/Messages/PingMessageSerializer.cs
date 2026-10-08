// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Messages
{
    public class PingMessageSerializer : IZeroMessageSerializer<PingMessage>
    {
        public void Serialize(Span<byte> buffer, PingMessage message)
        {
            RlpWriter writer = new(buffer);
            writer.StartSequence(0);
        }

        public int GetLength(PingMessage message, out int contentLength)
        {
            contentLength = 0;
            return Rlp.LengthOfSequence(contentLength);
        }

        public PingMessage Deserialize(ReadOnlySpan<byte> data, out int consumed)
        {
            RlpReader ctx = new(data);
            ctx.ReadSequenceLength();
            consumed = ctx.Position;
            return PingMessage.Instance;
        }
    }
}
