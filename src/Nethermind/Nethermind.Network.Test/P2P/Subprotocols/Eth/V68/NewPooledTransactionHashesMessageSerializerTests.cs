// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading.Tasks;
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
    private static void Test(TxType[] types, int[] sizes, Hash256[] hashes, string expected = null)
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
    public void Empty_hashes_serialization()
    {
        TxType[] types = { TxType.EIP1559 };
        int[] sizes = { 10 };
        Hash256[] hashes = [];
        Test(types, sizes, hashes, "c402c10ac0");
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

    [Test]
    public void Deserialize_throws_on_null_hash()
    {
        TxType[] types = { TxType.EIP1559 };
        int[] sizes = { 10 };
        Hash256[] hashes = { null! };
        using NewPooledTransactionHashesMessage68 message = new(
            types.Select(static t => (byte)t).ToPooledList(types.Length),
            sizes.ToPooledList(),
            hashes.ToPooledList());
        NewPooledTransactionHashesMessageSerializer serializer = new();

        byte[] bytes = serializer.Serialize(message);

        Assert.That(() => serializer.Deserialize(bytes), Throws.InstanceOf<RlpException>());
    }

    [Test]
    [NonParallelizable]
    public void Reused_message_clears_fields_and_lists([Values(0, 3, 129)] int count)
    {
        NewPooledTransactionHashesMessageSerializer serializer = new();
        using NewPooledTransactionHashesMessage68 source = new(
            Enumerable.Repeat((byte)2, count).ToPooledList(count),
            Enumerable.Repeat(100, count).ToPooledList(count),
            Enumerable.Repeat(TestItem.KeccakA, count).ToPooledList(count));
        byte[] bytes = serializer.Serialize(source);
        NewPooledTransactionHashesMessage68 first = serializer.Deserialize(bytes);
        first.AdaptivePacketType = 123;
        first.Dispose();
        first.Dispose();

        using NewPooledTransactionHashesMessage68 empty = serializer.Deserialize(Convert.FromHexString("c380c0c0"));
        using NewPooledTransactionHashesMessage68 next = serializer.Deserialize(bytes);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(empty.Hashes, Is.Empty);
            Assert.That(empty.Types, Is.Empty);
            Assert.That(empty.Sizes, Is.Empty);
            Assert.That(empty.AdaptivePacketType, Is.Zero);
            Assert.That(next.Hashes, Is.EqualTo(source.Hashes));
            Assert.That(next.Types, Is.EqualTo(source.Types));
            Assert.That(next.Sizes, Is.EqualTo(source.Sizes));
            Assert.That(next, Is.Not.SameAs(empty));
            Assert.That(empty, count <= 128 ? Is.SameAs(first) : Is.Not.SameAs(first));
        }
    }

    [Test]
    [NonParallelizable]
    public void Partial_decode_failure_does_not_contaminate_next_message(
        [Values("c502c164c180", "c502c164c1c0", "c402c1c0c0", "c502c164c181")] string malformed)
    {
        NewPooledTransactionHashesMessageSerializer serializer = new();
        Assert.That(() => serializer.Deserialize(Convert.FromHexString(malformed)), Throws.InstanceOf<RlpException>());
        using NewPooledTransactionHashesMessage68 next = serializer.Deserialize(Convert.FromHexString("c380c0c0"));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(next.Types, Is.Empty);
            Assert.That(next.Sizes, Is.Empty);
            Assert.That(next.Hashes, Is.Empty);
        }
    }

    [Test]
    public void Concurrent_batches_keep_independent_message_leases()
    {
        NewPooledTransactionHashesMessageSerializer serializer = new();
        byte[] bytes = Convert.FromHexString("c402c164c0");
        Parallel.For(0, 8, _ =>
        {
            NewPooledTransactionHashesMessage68[] messages = new NewPooledTransactionHashesMessage68[64];
            try
            {
                for (int i = 0; i < messages.Length; i++) messages[i] = serializer.Deserialize(bytes);
                Assert.That(messages.Distinct().Count(), Is.EqualTo(messages.Length));
                foreach (NewPooledTransactionHashesMessage68 message in messages)
                {
                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(message.Types, Is.EqualTo(new byte[] { 2 }));
                        Assert.That(message.Sizes, Is.EqualTo(new[] { 100 }));
                        Assert.That(message.Hashes, Is.Empty);
                    }
                }
            }
            finally
            {
                foreach (NewPooledTransactionHashesMessage68 message in messages) message?.Dispose();
            }
        });
    }

}
