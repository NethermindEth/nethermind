// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Subprotocols.Messages;
using Nethermind.Network;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Subprotocols.Serializers
{
    public class DataRequestResultMessageSerializer : IMessageSerializer<DataRequestResultMessage>
    {
        public byte[] Serialize(DataRequestResultMessage message)
            => Rlp.Encode(Rlp.Encode(message.DepositId),
                Rlp.Encode((int)message.Result)).Bytes;

        public DataRequestResultMessage Deserialize(byte[] bytes)
        {
            var context = bytes.AsRlpStream();
            context.ReadSequenceLength();
            var depositId = context.DecodeKeccak();
            var result = (DataRequestResult)context.DecodeUInt();

            return new DataRequestResultMessage(depositId, result);
        }
    }
}
