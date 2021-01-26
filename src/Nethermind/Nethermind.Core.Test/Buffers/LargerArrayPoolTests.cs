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
// 

using System;
using System.Buffers;
using System.Collections.Generic;
using Nethermind.Core.Buffers;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;

namespace Nethermind.Core.Test.Buffers
{
    public class LargerArrayPoolTests
    {
        private static readonly SingleArrayPool s_shared = new SingleArrayPool();
        private readonly ArrayPool<byte> _thrower;
        private const int ArrayPoolLimit = 16;
        private const int LargeBufferSize = 32;

        public LargerArrayPoolTests()
        {
            _thrower = Substitute.For<ArrayPool<byte>>();
            _thrower.Rent(Arg.Any<int>()).ThrowsForAnyArgs(new Exception());
        }

        [Test]
        public void Renting_small_goes_to_Small_Buffer_Pool()
        {
            LargerArrayPool pool = new LargerArrayPool(ArrayPoolLimit, LargeBufferSize, 1, s_shared);
            byte[] array = pool.Rent(ArrayPoolLimit);

            try
            {
                Assert.AreEqual(s_shared._bytes, array);
            }
            finally
            {
                pool.Return(array);
            }
        }

        [Test]
        public void Renting_bigger_uses_Larger_Pool()
        {
            const int middleSize = (ArrayPoolLimit + LargeBufferSize) / 2;

            LargerArrayPool pool = new LargerArrayPool(ArrayPoolLimit, LargeBufferSize, 1, _thrower);

            byte[] array1 = pool.Rent(middleSize);
            pool.Return(array1);

            byte[] array2 = pool.Rent(middleSize);

            Assert.True(ReferenceEquals(array1, array2));
        }

        [Test]
        public void Renting_above_Large_Buffer_Size_just_allocates()
        {
            const int tooBig = LargeBufferSize + 1;

            LargerArrayPool pool = new LargerArrayPool(ArrayPoolLimit, LargeBufferSize, 1, _thrower);
            byte[] array1 = pool.Rent(tooBig);
            pool.Return(array1);

            byte[] array2 = pool.Rent(tooBig);

            Assert.False(ReferenceEquals(array1, array2));
        }

        [Test]
        public void Renting_too_many_just_allocates()
        {
            const int middleSize = (ArrayPoolLimit + LargeBufferSize) / 2;

            const int size = 1;
            const int additional = 2;

            LargerArrayPool pool = new LargerArrayPool(ArrayPoolLimit, LargeBufferSize, size, _thrower);

            HashSet<byte[]> arrays = new HashSet<byte[]>();

            // rent first size + additional
            for (int i = 0; i < size + additional; i++)
            {
                arrays.Add(pool.Rent(middleSize));
            }

            // return all
            foreach (byte[] bytes in arrays)
            {
                pool.Return(bytes);
            }

            // try to train it all
            while (arrays.Remove(pool.Rent(middleSize)))
            {
            }

            Assert.AreEqual(additional, arrays.Count);
        }

        class SingleArrayPool : ArrayPool<byte>
        {
            public readonly byte[] _bytes = new byte[0];

            public override byte[] Rent(int minimumLength) => _bytes;

            public override void Return(byte[] array, bool clearArray = false) => Assert.AreEqual(_bytes, array);
        }
    }
}
