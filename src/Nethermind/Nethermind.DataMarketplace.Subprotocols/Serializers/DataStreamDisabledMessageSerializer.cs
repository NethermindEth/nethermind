// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Subprotocols.Messages;
using Nethermind.Network;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Subprotocols.Serializers
{
    public class DataStreamDisabledMessageSerializer : IMessageSerializer<DataStreamDisabledMessage>
    {
        public byte[] Serialize(DataStreamDisabledMessage message)
            => Rlp.Encode(Rlp.Encode(message.DepositId),
                Rlp.Encode(message.Client)).Bytes;

        public DataStreamDisabledMessage Deserialize(byte[] bytes)
        {
            RlpStream context = bytes.AsRlpStream();
            context.ReadSequenceLength();
            Keccak? depositId = context.DecodeKeccak();
            string client = context.DecodeString();

            return new DataStreamDisabledMessage(depositId, client);
        }
    }
}
