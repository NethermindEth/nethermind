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

using System.Diagnostics;

namespace Nethermind.Trie
{
    [DebuggerDisplay("{_nibble}")]
    [DebuggerStepThrough]
    public struct Nibble
    {
        // ReSharper disable once FieldCanBeMadeReadOnly.Local
        private byte _nibble;

        public Nibble(char hexChar)
        {
            hexChar = char.ToUpper(hexChar);
            _nibble = hexChar < 'A'? (byte) (hexChar - '0') : (byte) (10 + (hexChar - 'A'));
        }

        public Nibble(byte nibble)
        {
            _nibble = nibble;
        }

        public static explicit operator byte(Nibble nibble)
        {
            return nibble._nibble;
        }

        public static implicit operator Nibble(byte nibbleValue)
        {
            return new(nibbleValue);
        }

        public static implicit operator Nibble(char hexChar)
        {
            return new(hexChar);
        }
    }
}
