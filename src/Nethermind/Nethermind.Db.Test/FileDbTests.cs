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
using System.Linq;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.Db.Test
{
    [TestFixture]
    public class FileDbTests
    {
        [Test]
        public void Smoke_test()
        {
            using Files.Db db = new("blocks");
            byte[] key = Keccak.Compute("key").Bytes;

            db[key] = new byte[] { 4, 5, 6 };

            Assert.AreEqual(new byte[] { 4, 5, 6 }, db[key.ToArray()]);
        }

        [Test]
        public void Smoke_test_2()
        {
            const int size = 2_000_000;

            static byte[] Key(int i) => Keccak.Compute(i.ToString()).Bytes;
            static byte[] Value(int i) => BitConverter.GetBytes(i);

            using Files.Db db = new("blocks", 1024 * 1024, size * 2);
            for (int i = 0; i < size; i++)
            {
                db[Key(i)] = Value(i);
            }

            for (int i = size / 2; i < size; i++)
            {
                CollectionAssert.AreEqual(Value(i), db[Key(i)]);
            }
        }
    }
}
