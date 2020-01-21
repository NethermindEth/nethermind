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

using System;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Serialization.Rlp
{
    public static class SignatureDecoder
    {
        public static Signature DecodeSignature(RlpStream rlpStream)
        {
            Span<byte> vBytes = rlpStream.DecodeByteArraySpan();
            Span<byte> rBytes = rlpStream.DecodeByteArraySpan();
            Span<byte> sBytes = rlpStream.DecodeByteArraySpan();

            if (vBytes[0] == 0 || rBytes[0] == 0 || sBytes[0] == 0)
            {
                throw new RlpException("VRS starting with 0");
            }

            if (rBytes.Length > 32 || sBytes.Length > 32)
            {
                throw new RlpException("R and S lengths expected to be less or equal 32");
            }

            int v = vBytes.ReadEthInt32();

            if (rBytes.SequenceEqual(Bytes.Zero32) && sBytes.SequenceEqual(Bytes.Zero32))
            {
                throw new RlpException("Both 'r' and 's' are zero when decoding a transaction.");
            }

            Signature signature = new Signature(rBytes, sBytes, v);
            return signature;
        }
    }
}