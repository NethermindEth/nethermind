// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.DataMarketplace.Subprotocols.Messages;
using Nethermind.Network;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Subprotocols.Serializers
{
    public class FinishSessionMessageSerializer : IMessageSerializer<FinishSessionMessage>
    {
        public byte[] Serialize(FinishSessionMessage message)
            => Rlp.Encode(Rlp.Encode(message.DepositId)).Bytes;

        public FinishSessionMessage Deserialize(byte[] bytes)
        {
            var context = bytes.AsRlpStream();
            context.ReadSequenceLength();
            var depositId = context.DecodeKeccak();

            return new FinishSessionMessage(depositId);
        }
    }
}
