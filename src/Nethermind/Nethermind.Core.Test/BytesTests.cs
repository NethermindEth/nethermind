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
using System.Collections.Generic;
using System.IO;
using System.Net.Http.Headers;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.Core.Test
{
    [TestFixture]
    public class BytesTests
    {
        [TestCase("0x", "0x", 0)]
        [TestCase(null, null, 0)]
        [TestCase(null, "0x", 1)]
        [TestCase("0x", null, -1)]
        [TestCase("0x01", "0x01", 0)]
        [TestCase("0x01", "0x0102", 1)]
        [TestCase("0x0102", "0x01", -1)]
        public void Compares_bytes_properly(string hexString1, string hexString2, int expectedResult)
        {
            IComparer<byte[]> comparer = Bytes.Comparer;
            byte[] x = hexString1 == null ? null : Bytes.FromHexString(hexString1);
            byte[] y = hexString2 == null ? null : Bytes.FromHexString(hexString2);
            Assert.AreEqual(expectedResult, comparer.Compare(x, y));
        }
        
        [TestCase("0x", "0x", true)]
        [TestCase(null, null, true)]
//        [TestCase(null, "0x", false)]
//        [TestCase("0x", null, false)]
        [TestCase("0x01", "0x01", true)]
        [TestCase("0x01", "0x0102", false)]
        [TestCase("0x0102", "0x01", false)]
        public void Compares_bytes_equality_properly(string hexString1, string hexString2, bool expectedResult)
        {
            // interestingly, sequence equals that we have been using for some time returns 0x == null, null == 0x
            IEqualityComparer<byte[]> comparer = Bytes.EqualityComparer;
            byte[] x = hexString1 == null ? null : Bytes.FromHexString(hexString1);
            byte[] y = hexString2 == null ? null : Bytes.FromHexString(hexString2);
            Assert.AreEqual(expectedResult, comparer.Equals(x, y));
        }
        
        [Test]
        public void Stream_hex_works()
        {
            byte[] bytes = new byte[] {15, 16, 255};
            StreamWriter sw = null;
            StreamReader sr = null;

            try
            {
                using (var ms = new MemoryStream())
                {
                    sw = new StreamWriter(ms);
                    sr = new StreamReader(ms);

                    bytes.StreamHex(sw);
                    sw.Flush();
                    
                    ms.Position = 0;

                    string result = sr.ReadToEnd();
                    Assert.AreEqual("0f10ff", result);
                }
            }
            finally
            {
                sw?.Dispose();
                sr?.Dispose();
            }
        }
    }
}