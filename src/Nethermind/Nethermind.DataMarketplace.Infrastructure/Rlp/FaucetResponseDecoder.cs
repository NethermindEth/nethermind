// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Infrastructure.Rlp
{
    public class FaucetResponseDecoder : IRlpNdmDecoder<FaucetResponse>
    {
        public static void Init()
        {
            // here to register with RLP in static constructor
        }

        static FaucetResponseDecoder()
        {
            Serialization.Rlp.Rlp.Decoders[typeof(FaucetResponse)] = new FaucetResponseDecoder();
        }

        public FaucetResponse Decode(RlpStream rlpStream,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            rlpStream.ReadSequenceLength();
            FaucetRequestStatus status = (FaucetRequestStatus)rlpStream.DecodeInt();
            FaucetRequestDetails request = Serialization.Rlp.Rlp.Decode<FaucetRequestDetails>(rlpStream);

            return new FaucetResponse(status, request);
        }

        public void Encode(RlpStream stream, FaucetResponse item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            throw new System.NotImplementedException();
        }

        public Serialization.Rlp.Rlp Encode(FaucetResponse item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                return Serialization.Rlp.Rlp.OfEmptySequence;
            }

            return Serialization.Rlp.Rlp.Encode(
                Serialization.Rlp.Rlp.Encode((int)item.Status),
                Serialization.Rlp.Rlp.Encode(item.LatestRequest));
        }

        public int GetLength(FaucetResponse item, RlpBehaviors rlpBehaviors)
        {
            throw new System.NotImplementedException();
        }
    }
}
