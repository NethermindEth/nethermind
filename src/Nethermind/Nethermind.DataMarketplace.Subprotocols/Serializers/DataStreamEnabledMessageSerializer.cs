// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Subprotocols.Messages;
using Nethermind.Network;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Subprotocols.Serializers
{
    public class DataStreamEnabledMessageSerializer : IMessageSerializer<DataStreamEnabledMessage>
    {
        public byte[] Serialize(DataStreamEnabledMessage message)
            => Rlp.Encode(Rlp.Encode(message.DepositId),
                Rlp.Encode(message.Client),
                Rlp.Encode(message.Args)).Bytes;

        public DataStreamEnabledMessage Deserialize(byte[] bytes)
        {
            RlpStream context = bytes.AsRlpStream();
            context.ReadSequenceLength();
            Keccak depositId = context.DecodeKeccak();
            string client = context.DecodeString();
            string?[] args = context.DecodeArray(c => c.DecodeString());

            return new DataStreamEnabledMessage(depositId, client, args);
        }
    }
}
