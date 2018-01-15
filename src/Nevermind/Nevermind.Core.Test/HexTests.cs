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

using Nevermind.Core.Encoding;
using NUnit.Framework;

namespace Nevermind.Core.Test
{
    [TestFixture]
    public class HexTests
    {
        [TestCase("0x1")]
        [TestCase("0x123")]
        [TestCase("0x12345")]
        [TestCase("0x0")]
        [TestCase("0x12")]
        [TestCase("0x1234")]
        [TestCase("1")]
        [TestCase("123")]
        [TestCase("12345")]
        [TestCase("0")]
        [TestCase("12")]
        [TestCase("1234")]
        public void Can_convert_from_string_and_back_when_leading_zeros_are_missing(string hexString)
        {
            bool withZeroX = hexString.StartsWith("0x");
            Hex hex = new Hex(hexString);
            Assert.AreEqual(hexString, hex.ToString(withZeroX, true), $"held as {nameof(Hex)} from string");

            byte[] bytes = Hex.ToBytes(hexString);
            string result = Hex.FromBytes(bytes, withZeroX, true);
            Assert.AreEqual(hexString, result, "converted twice");

            hex = new Hex(bytes);
            Assert.AreEqual(hexString, hex.ToString(withZeroX, true), $"held as {nameof(Hex)} from bytes");
        }

        [TestCase("0x0", 1)]
        [TestCase("0x12", 2)]
        [TestCase("0x1234", 4)]
        [TestCase("0", 1)]
        [TestCase("12", 2)]
        [TestCase("1234", 4)]
        public void Can_extract_nibbles(string hexString, int nibbleCount)
        {
            Nibble[] nibbles = Hex.ToNibbles(hexString);
            Assert.AreEqual(nibbleCount, nibbles.Length);
        }
    }
}