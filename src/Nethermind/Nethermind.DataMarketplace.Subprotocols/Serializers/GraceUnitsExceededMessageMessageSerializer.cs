// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.DataMarketplace.Subprotocols.Messages;
using Nethermind.Network;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Subprotocols.Serializers
{
    public class GraceUnitsExceededMessageMessageSerializer : IMessageSerializer<GraceUnitsExceededMessage>
    {
        public byte[] Serialize(GraceUnitsExceededMessage message)
            => Rlp.Encode(Rlp.Encode(message.DepositId),
                Rlp.Encode(message.ConsumedUnits),
                Rlp.Encode(message.GraceUnits)).Bytes;

        public GraceUnitsExceededMessage Deserialize(byte[] bytes)
        {
            var context = bytes.AsRlpStream();
            context.ReadSequenceLength();
            var depositId = context.DecodeKeccak();
            var consumedUnits = context.DecodeUInt();
            var graceUnits = context.DecodeUInt();

            return new GraceUnitsExceededMessage(depositId, consumedUnits, graceUnits);
        }
    }
}
