// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using Nethermind.Core.Buffers;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

public class TrackedPooledCappedArrayPoolTests
{
    [Test]
    public void Test_Pooling()
    {
        ArrayPool<byte>? arrayPool = Substitute.For<ArrayPool<byte>>();
        arrayPool
            .Rent(Arg.Any<int>())
            .Returns<byte[]>(info => new byte[(int)info[0]]);
        TrackedPooledCappedArrayPool? pool = new TrackedPooledCappedArrayPool(0, arrayPool);

        pool.Rent(1);
        pool.Rent(1);
        pool.Rent(1);
        CappedArray<byte> sample = pool.Rent(1);

        arrayPool.Received(4).Rent(1);

        pool.Return(sample);
        arrayPool.Received(0).Return(Arg.Any<byte[]>());

        pool.ReturnAll();
        arrayPool.Received(4).Return(Arg.Any<byte[]>());
    }
}
