// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Messages
{
    public class PingMessageSerializer : IMessageSerializer<PingMessage>
    {
        public int GetLength(PingMessage message) => 1;

        public int Serialize(Span<byte> destination, PingMessage message)
        {
            destination[0] = Rlp.EmptyListByte;
            return 1;
        }

        public PingMessage Deserialize(ReadOnlySpan<byte> data) => PingMessage.Instance;
    }
}
