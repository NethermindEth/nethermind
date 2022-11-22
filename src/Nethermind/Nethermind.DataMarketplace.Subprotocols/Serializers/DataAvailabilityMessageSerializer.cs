// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Subprotocols.Messages;
using Nethermind.Network;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Subprotocols.Serializers
{
    public class DataAvailabilityMessageSerializer : IMessageSerializer<DataAvailabilityMessage>
    {
        public byte[] Serialize(DataAvailabilityMessage message)
            => Rlp.Encode(Rlp.Encode(message.DepositId),
                Rlp.Encode((int)message.DataAvailability)).Bytes;

        public DataAvailabilityMessage Deserialize(byte[] bytes)
        {
            RlpStream context = bytes.AsRlpStream();
            context.ReadSequenceLength();
            Keccak? depositId = context.DecodeKeccak();
            DataAvailability reason = (DataAvailability)context.DecodeInt();

            return new DataAvailabilityMessage(depositId, reason);
        }
    }
}
