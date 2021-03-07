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

using System;

namespace Nethermind.Serialization.Rlp
{
    public static class ByteArrayExtensions
    {
        public static RlpStream AsRlpStream(this byte[]? bytes)
        {
            return new(bytes ?? Array.Empty<byte>());
        }

        public static Rlp.ValueDecoderContext AsRlpValueContext(this byte[]? bytes)
        {
            return new(bytes ?? Array.Empty<byte>());
        }

        public static Rlp.ValueDecoderContext AsRlpValueContext(this Span<byte> span)
        {
            return span.IsEmpty ? new Rlp.ValueDecoderContext(Array.Empty<byte>()) : new Rlp.ValueDecoderContext(span);
        }
    }
}
