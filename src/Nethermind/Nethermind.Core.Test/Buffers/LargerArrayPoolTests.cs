// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
        private static readonly SingleArrayPool s_shared = new();
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
            LargerArrayPool pool = new(ArrayPoolLimit, LargeBufferSize, 1, s_shared);
            byte[] array = pool.Rent(ArrayPoolLimit);

            try
            {
                Assert.That(array, Is.EqualTo(s_shared._bytes));
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

            LargerArrayPool pool = new(ArrayPoolLimit, LargeBufferSize, 1, _thrower);

            byte[] array1 = pool.Rent(middleSize);
            pool.Return(array1);

            byte[] array2 = pool.Rent(middleSize);

            Assert.True(ReferenceEquals(array1, array2));
        }

        [Test]
        public void Renting_above_Large_Buffer_Size_just_allocates()
        {
            const int tooBig = LargeBufferSize + 1;

            LargerArrayPool pool = new(ArrayPoolLimit, LargeBufferSize, 1, _thrower);
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

            LargerArrayPool pool = new(ArrayPoolLimit, LargeBufferSize, size, _thrower);

            HashSet<byte[]> arrays = new();

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

            Assert.That(arrays.Count, Is.EqualTo(additional));
        }

        class SingleArrayPool : ArrayPool<byte>
        {
            public readonly byte[] _bytes = new byte[0];

            public override byte[] Rent(int minimumLength) => _bytes;

            public override void Return(byte[] array, bool clearArray = false) => Assert.That(array, Is.EqualTo(_bytes));
        }
    }
}
