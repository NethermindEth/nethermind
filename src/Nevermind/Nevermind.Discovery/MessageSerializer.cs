/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using Nevermind.Discovery.Messages;

namespace Nevermind.Discovery
{
    public class MessageSerializer : IMessageSerializer
    {
        public byte[] Serialize(Message message)
        {
            throw new NotImplementedException();
            
//            /* [1] Calc keccak - prepare for sig */
//            byte[] payload = new byte[type.length + data.length];
//            payload[0] = type[0];
//            System.arraycopy(data, 0, payload, 1, data.length);
//            byte[] forSig = sha3(payload);
//
//            /* [2] Crate signature*/
//            ECKey.ECDSASignature signature = privKey.sign(forSig);
//
//            signature.v -= 27;
//
//            byte[] sigBytes =
//                merge(BigIntegers.asUnsignedByteArray(32, signature.r),
//                    BigIntegers.asUnsignedByteArray(32, signature.s), new byte[]{signature.v});
//
//            // [3] calculate MDC
//            byte[] forSha = merge(sigBytes, type, data);
//            byte[] mdc = sha3(forSha);
//
//            // wrap all the data in to the packet
//            this.mdc = mdc;
//            this.signature = sigBytes;
//            this.type = type;
//            this.data = data;
//
//            this.wire = merge(this.mdc, this.signature, this.type, this.data);
//
//            return this;
        }

        public Message Deserialize(byte[] message)
        {
            throw new NotImplementedException();
        }
    }
}