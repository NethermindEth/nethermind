// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Subprotocols.Messages;
using Nethermind.Network;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Subprotocols.Serializers
{
    public class RequestDepositApprovalMessageSerializer : IMessageSerializer<RequestDepositApprovalMessage>
    {
        public byte[] Serialize(RequestDepositApprovalMessage message)
            => Rlp.Encode(Rlp.Encode(message.DataAssetId),
                Rlp.Encode(message.Consumer),
                Rlp.Encode(message.Kyc)).Bytes;

        public RequestDepositApprovalMessage Deserialize(byte[] bytes)
        {
            RlpStream context = bytes.AsRlpStream();
            context.ReadSequenceLength();
            Keccak? dataAssetId = context.DecodeKeccak();
            Address? consumer = context.DecodeAddress();
            string kyc = context.DecodeString();

            return new RequestDepositApprovalMessage(dataAssetId, consumer, kyc);
        }
    }
}
