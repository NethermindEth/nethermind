// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Eth.V68.Messages;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V68;

[TestFixture, Parallelizable(ParallelScope.All)]
public class NewPooledTransactionHashesMessageSerializerTests
{
    private static void Test(TxType[] types, int[] sizes, Hash256[] hashes, string expected = null)
    {
        using NewPooledTransactionHashesMessage68 message = new(types.Select(t => (byte)t).ToPooledList(types.Length), sizes.ToPooledList(), hashes.ToPooledList());
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
        TxType[] types = { };
        int[] sizes = { };
        Hash256[] hashes = { };
        Test(types, sizes, hashes, "c380c0c0");
    }

    [Test]
    public void Empty_hashes_serialization()
    {
        TxType[] types = { TxType.EIP1559 };
        int[] sizes = { 10 };
        Hash256[] hashes = { };
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

}
