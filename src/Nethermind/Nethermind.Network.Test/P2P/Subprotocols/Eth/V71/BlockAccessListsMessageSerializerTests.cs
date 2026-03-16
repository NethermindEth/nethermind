// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Eth.V71.Messages;
using Nethermind.Network.Test.P2P.Subprotocols.Eth.V62;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V71;

[Parallelizable(ParallelScope.All)]
public class BlockAccessListsMessageSerializerTests
{
    [Test]
    public void Roundtrip_empty()
    {
        BlockAccessListsMessageSerializer serializer = new();
        using BlockAccessListsMessage msg = new(ArrayPoolList<byte[]>.Empty());
        SerializerTester.TestZero(serializer, msg);
    }

    [Test]
    public void Roundtrip_single_empty_bal()
    {
        BlockAccessListsMessageSerializer serializer = new();
        ArrayPoolList<byte[]> bals = new(1) { BlockAccessListsMessage.EmptyBal };
        using BlockAccessListsMessage msg = new(bals);
        SerializerTester.TestZero(serializer, msg);
    }

    [Test]
    public void Roundtrip_multiple_bals()
    {
        BlockAccessListsMessageSerializer serializer = new();
        ArrayPoolList<byte[]> bals = new(3);
        bals.Add(new byte[] { 0xc1, 0x80 });
        bals.Add(new byte[] { 0xc2, 0x01, 0x02 });
        bals.Add(BlockAccessListsMessage.EmptyBal);
        using BlockAccessListsMessage msg = new(bals);
        SerializerTester.TestZero(serializer, msg);
    }

    [Test]
    public void Roundtrip_66_empty()
    {
        BlockAccessListsMessageSerializer innerSerializer = new();
        BlockAccessListsMessageSerializer66 serializer = new(innerSerializer);
        using BlockAccessListsMessage inner = new(ArrayPoolList<byte[]>.Empty());
        BlockAccessListsMessage66 msg = new(42, inner);
        SerializerTester.TestZero(serializer, msg);
    }

    [Test]
    public void Roundtrip_66_with_data()
    {
        BlockAccessListsMessageSerializer innerSerializer = new();
        BlockAccessListsMessageSerializer66 serializer = new(innerSerializer);
        ArrayPoolList<byte[]> bals = new(2);
        bals.Add(new byte[] { 0xc1, 0x80 });
        bals.Add(new byte[] { 0xc3, 0x01, 0x02, 0x03 });
        using BlockAccessListsMessage inner = new(bals);
        BlockAccessListsMessage66 msg = new(12345, inner);
        SerializerTester.TestZero(serializer, msg);
    }
}

[Parallelizable(ParallelScope.All)]
public class GetBlockAccessListsMessageSerializerTests
{
    [Test]
    public void Roundtrip_empty_hashes()
    {
        GetBlockAccessListsMessageSerializer serializer = new();
        using GetBlockAccessListsMessage msg = new(new ArrayPoolList<Hash256>(0));
        SerializerTester.TestZero(serializer, msg);
    }

    [Test]
    public void Roundtrip_single_hash()
    {
        GetBlockAccessListsMessageSerializer serializer = new();
        using GetBlockAccessListsMessage msg = new(new[] { Keccak.Zero }.ToPooledList());
        SerializerTester.TestZero(serializer, msg);
    }

    [Test]
    public void Roundtrip_multiple_hashes()
    {
        GetBlockAccessListsMessageSerializer serializer = new();
        using GetBlockAccessListsMessage msg = new(new Hash256[] { Keccak.Zero, TestItem.KeccakA, TestItem.KeccakB }.ToPooledList(3));
        SerializerTester.TestZero(serializer, msg);
    }

    [Test]
    public void Roundtrip_66_empty()
    {
        GetBlockAccessListsMessageSerializer innerSerializer = new();
        GetBlockAccessListsMessageSerializer66 serializer = new(innerSerializer);
        using GetBlockAccessListsMessage inner = new(new ArrayPoolList<Hash256>(0));
        GetBlockAccessListsMessage66 msg = new(99, inner);
        SerializerTester.TestZero(serializer, msg);
    }

    [Test]
    public void Roundtrip_66_with_hashes()
    {
        GetBlockAccessListsMessageSerializer innerSerializer = new();
        GetBlockAccessListsMessageSerializer66 serializer = new(innerSerializer);
        using GetBlockAccessListsMessage inner = new(new Hash256[] { Keccak.Zero, TestItem.KeccakA }.ToPooledList(2));
        GetBlockAccessListsMessage66 msg = new(7777, inner);
        SerializerTester.TestZero(serializer, msg);
    }
}
