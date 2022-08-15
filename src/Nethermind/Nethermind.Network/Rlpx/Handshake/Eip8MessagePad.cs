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

using Nethermind.Core.Extensions;
using Nethermind.Crypto;

namespace Nethermind.Network.Rlpx.Handshake
{
    public class Eip8MessagePad : IMessagePad
    {
        readonly ICryptoRandom _cryptoRandom;

        public Eip8MessagePad(ICryptoRandom cryptoRandom)
        {
            _cryptoRandom = cryptoRandom;
        }

        public byte[] Pad(byte[] message)
        {
            byte[] padding = _cryptoRandom.GenerateRandomBytes(100 + _cryptoRandom.NextInt(201));
            return Bytes.Concat(message, padding);
        }
    }
}
