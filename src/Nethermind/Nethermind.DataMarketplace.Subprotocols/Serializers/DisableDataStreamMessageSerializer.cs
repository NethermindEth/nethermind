// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.DataMarketplace.Subprotocols.Messages;
using Nethermind.Network;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Subprotocols.Serializers
{
    public class DisableDataStreamMessageSerializer : IMessageSerializer<DisableDataStreamMessage>
    {
        public byte[] Serialize(DisableDataStreamMessage message)
            => Rlp.Encode(Rlp.Encode(message.DepositId),
                Rlp.Encode(message.Client)).Bytes;

        public DisableDataStreamMessage Deserialize(byte[] bytes)
        {
            var context = bytes.AsRlpStream();
            context.ReadSequenceLength();
            var depositId = context.DecodeKeccak();
            var client = context.DecodeString();

            return new DisableDataStreamMessage(depositId, client);
        }
    }
}
