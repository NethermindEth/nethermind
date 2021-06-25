//  Copyright (c) 2021 Demerzel Solutions Limited
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
// 

using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Mev.Data
{
    public partial class MevBundle
    {
        private static Keccak GetHash(MevBundle bundle)
        {
            RlpStream stream = EncodeRlp(bundle);
            return Keccak.Compute(stream.Data);
        }

        private static RlpStream EncodeRlp(MevBundle bundle)
        {
            (int Content, int Tx) GetContentLength()
            {
                int txHashes = Rlp.LengthOfKeccakRlp * bundle.Transactions.Count;
                int content = Rlp.LengthOf(bundle.BlockNumber) + Rlp.GetSequenceRlpLength(txHashes);
                return (Rlp.LengthOfSequence(content), txHashes);
            }
            
            (int contentLength, int txLength) = GetContentLength();
            RlpStream stream = new(contentLength);
            stream.StartSequence(contentLength);
            stream.Encode(bundle.BlockNumber);
            stream.StartSequence(txLength);

            for (int i = 0; i < bundle.Transactions.Count; i++)
            {
                stream.Encode(bundle.Transactions[i].Hash);
            }

            return stream;
        }
    }
}
