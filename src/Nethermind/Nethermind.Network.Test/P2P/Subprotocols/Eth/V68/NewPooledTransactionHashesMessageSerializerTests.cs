// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Eth.V68.Messages;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V68;

[TestFixture, Parallelizable(ParallelScope.All)]
public class NewPooledTransactionHashesMessageSerializerTests
{
    private static void Test(TxType[] types, int[] sizes, Hash256[] hashes, string? expected = null)
    {
        using NewPooledTransactionHashesMessage68 message = new(types.Select(static t => (byte)t).ToPooledList(types.Length), sizes.ToPooledList(), hashes.ToPooledList());
        NewPooledTransactionHashesMessageSerializer serializer = new();

        SerializerTester.TestZero(serializer, message, expected);
    }

    [Test]
    public void Roundtrip()
    {
        TxType[] types = { TxType.Legacy, TxType.AccessList, TxType.EIP1559 };
        int[] sizes = { 5, 10, 1500 };
        Hash256[] hashes = { TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC };
        Test(types, sizes, hashes);
    }

    [Test]
    public void Empty_serialization()
    {
        TxType[] types = [];
        int[] sizes = [];
        Hash256[] hashes = [];
        Test(types, sizes, hashes, "c380c0c0");
    }

    [Test]
    public void Non_empty_serialization()
    {
        TxType[] types = { TxType.AccessList };
        int[] sizes = { 2 };
        Hash256[] hashes = { TestItem.KeccakA };
        Test(types, sizes, hashes,
            "e5" + "01" + "c102" + "e1a0" + TestItem.KeccakA.ToString(false));
    }

    // Most other clients throw on mismatched arrays
    [TestCase(2, 1, 1)]
    [TestCase(1, 2, 1)]
    [TestCase(1, 1, 2)]
    public void Deserialize_mismatched_array_lengths_throws(int typeCount, int sizeCount, int hashCount)
    {
        using NewPooledTransactionHashesMessage68 message = new(
            types: Enumerable.Repeat((byte)TxType.Legacy, typeCount).ToPooledList(typeCount),
            sizes: Enumerable.Repeat(100, sizeCount).ToPooledList(sizeCount),
            hashes: Enumerable.Repeat(TestItem.KeccakA, hashCount).ToPooledList(hashCount)
        );

        NewPooledTransactionHashesMessageSerializer serializer = new();

        using DisposableByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer(1024).AsDisposable();
        serializer.Serialize(buffer, message);

        Assert.Throws<RlpException>(() => serializer.Deserialize(buffer));
    }

}
