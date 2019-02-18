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
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.Core.Test
{
    [TestFixture]
    public class KeccakTests
    {
        public const string KeccakOfAnEmptyString = "0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470";
        public const string KeccakZero = "0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470";

        [Test]
        public void To_short_string()
        {
            string result = Keccak.OfAnEmptyString.ToShortString();
            Assert.AreEqual("c5d246...85a470", result);
        }

        [Test]
        public void Empty_byte_array()
        {
            string result = Keccak.Compute(new byte[] { }).ToString();
            Assert.AreEqual(KeccakOfAnEmptyString, result);
        }

        [Test]
        public void Empty_string()
        {
            string result = Keccak.Compute(string.Empty).ToString();
            Assert.AreEqual(KeccakOfAnEmptyString, result);
        }

        [Test]
        public void Null_string()
        {
            string result = Keccak.Compute((string)null).ToString();
            Assert.AreEqual(KeccakOfAnEmptyString, result);
        }

        [Test]
        public void Null_bytes()
        {
            string result = Keccak.Compute((byte[])null).ToString();
            Assert.AreEqual(KeccakOfAnEmptyString, result);
        }

        [Test]
        public void Zero()
        {
            string result = Keccak.Zero.ToString();
            Assert.AreEqual("0x0000000000000000000000000000000000000000000000000000000000000000", result);
        }

        [Test]
        public void Span()
        {
            byte[] byteArray = new byte[1024];
            for (int i = 0; i < byteArray.Length; i++)
            {
                byteArray[i] = (byte)(i % 256);
            }
            
            Assert.AreEqual(Keccak.Compute(byteArray), Keccak.Compute(byteArray.AsSpan()));
        }
    }
}