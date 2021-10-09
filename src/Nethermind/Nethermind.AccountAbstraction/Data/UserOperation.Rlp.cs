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
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.Eip2930;

namespace Nethermind.AccountAbstraction.Data
{
    public partial class UserOperation
    {
        public static Keccak CalculateHash(UserOperation userOperation) => Keccak.Compute(EncodeRlp(userOperation).Data);
        
        private static RlpStream EncodeRlp(UserOperation op)
        {
            AccessListDecoder accessListDecoder = new AccessListDecoder();

            int contentLength = GetContentLength(op, accessListDecoder);
            int sequenceLength = Rlp.GetSequenceRlpLength(contentLength);

            RlpStream stream = new(sequenceLength);
            stream.StartSequence(contentLength);
            
            stream.Encode(op.Sender);
            stream.Encode(op.Nonce);
            stream.Encode(op.CallData);
            stream.Encode(op.CallGas);
            stream.Encode(op.MaxFeePerGas);
            stream.Encode(op.MaxPriorityFeePerGas);
            stream.Encode(op.Paymaster);
            
            // signature encoding
            stream.Encode(op.Signature);
            
            return stream;
        }
        
        private static int GetContentLength(UserOperation op, AccessListDecoder accessListDecoder)
        {
            return Rlp.LengthOf(op.Sender)
                   + Rlp.LengthOf(op.Nonce)
                   + Rlp.LengthOf(op.CallData)
                   + Rlp.LengthOf(op.CallGas)
                   + Rlp.LengthOf(op.MaxFeePerGas)
                   + Rlp.LengthOf(op.MaxPriorityFeePerGas)
                   + Rlp.LengthOf(op.Paymaster)
                   + Rlp.LengthOf(op.Signature);
        }
    }
}
