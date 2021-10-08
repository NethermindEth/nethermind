//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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
            FaucetRequestStatus status = (FaucetRequestStatus) rlpStream.DecodeInt();
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
                Serialization.Rlp.Rlp.Encode((int) item.Status),
                Serialization.Rlp.Rlp.Encode(item.LatestRequest));
        }

        public int GetLength(FaucetResponse item, RlpBehaviors rlpBehaviors)
        {
            throw new System.NotImplementedException();
        }
    }
}
