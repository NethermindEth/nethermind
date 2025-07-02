// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;

namespace Nethermind.Trie
{
    [DebuggerDisplay("{_nibble}")]
    [DebuggerStepThrough]
    public readonly struct Nibble
    {
        // ReSharper disable once FieldCanBeMadeReadOnly.Local
        private readonly byte _nibble;

        public Nibble(char hexChar)
        {
            hexChar = char.ToUpper(hexChar);
            _nibble = hexChar < 'A' ? (byte)(hexChar - '0') : (byte)(10 + (hexChar - 'A'));
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
