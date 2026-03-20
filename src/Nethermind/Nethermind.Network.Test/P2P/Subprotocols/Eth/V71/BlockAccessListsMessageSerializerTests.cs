// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
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
        using BlockAccessListsMessage msg = new(42, ArrayPoolList<byte[]>.Empty());
        SerializerTester.TestZero(serializer, msg);
    }

    [Test]
    public void Roundtrip_single_empty_bal()
    {
        BlockAccessListsMessageSerializer serializer = new();
        ArrayPoolList<byte[]> bals = new(1) { BlockAccessListsMessage.EmptyBal };
        using BlockAccessListsMessage msg = new(43, bals);
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
        using BlockAccessListsMessage msg = new(44, bals);
        SerializerTester.TestZero(serializer, msg);
    }

    [Test]
    public void Rejects_extra_outer_payload()
    {
        BlockAccessListsMessageSerializer serializer = new();
        IByteBuffer payload = Unpooled.WrappedBuffer([0xc5, 0x01, 0xc2, 0x81, 0xc0, 0xc0]);

        Assert.Throws<RlpException>(() => serializer.Deserialize(payload));
    }

    [Test]
    public void Roundtrip_negative_request_id()
    {
        BlockAccessListsMessageSerializer serializer = new();
        using BlockAccessListsMessage msg = new(-1, ArrayPoolList<byte[]>.Empty());

        SerializerTester.TestZero(serializer, msg);
    }

    [Test]
    public void Rejects_request_id_longer_than_8_bytes()
    {
        BlockAccessListsMessageSerializer serializer = new();
        IByteBuffer payload = Unpooled.WrappedBuffer([0xcb, 0x89, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0xc0]);

        Assert.Throws<RlpException>(() => serializer.Deserialize(payload));
    }
}

[Parallelizable(ParallelScope.All)]
public class GetBlockAccessListsMessageSerializerTests
{
    [Test]
    public void Roundtrip_empty_hashes()
    {
        GetBlockAccessListsMessageSerializer serializer = new();
        using GetBlockAccessListsMessage msg = new(99, new ArrayPoolList<Hash256>(0));
        SerializerTester.TestZero(serializer, msg);
    }

    [Test]
    public void Roundtrip_single_hash()
    {
        GetBlockAccessListsMessageSerializer serializer = new();
        using GetBlockAccessListsMessage msg = new(100, new[] { Keccak.Zero }.ToPooledList());
        SerializerTester.TestZero(serializer, msg);
    }

    [Test]
    public void Roundtrip_multiple_hashes()
    {
        GetBlockAccessListsMessageSerializer serializer = new();
        using GetBlockAccessListsMessage msg = new(101, new Hash256[] { Keccak.Zero, TestItem.KeccakA, TestItem.KeccakB }.ToPooledList(3));
        SerializerTester.TestZero(serializer, msg);
    }
}
