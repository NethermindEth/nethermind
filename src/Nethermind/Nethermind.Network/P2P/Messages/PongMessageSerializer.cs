// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Messages
{
    public class PongMessageSerializer : IMessageSerializer<PongMessage>
    {
        public int GetLength(PongMessage message) => 1;

        public int Serialize(Span<byte> destination, PongMessage message)
        {
            destination[0] = Rlp.EmptyListByte;
            return 1;
        }

        public PongMessage Deserialize(ReadOnlySpan<byte> data) => PongMessage.Instance;
    }
}
