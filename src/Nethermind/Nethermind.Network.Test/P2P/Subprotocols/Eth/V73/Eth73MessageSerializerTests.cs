// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Network.P2P.Subprotocols.Eth.V72.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V73.Messages;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V73;

[TestFixture, Parallelizable(ParallelScope.All)]
public class Eth73MessageSerializerTests
{
    // [[0x02, 0x03], [1024, 1], [hash1, hash2], cells, [source1, source2], [0, 258]]
    private const string CanonicalAnnouncementRlp =
        "f88d820203c482040001f842a0000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1fa0202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f"
        + "9001000000000000000000000000000080ea940102030405060708090a0b0c0d0e0f101112131494ffffffffffffffffffffffffffffffffffffffffc480820102";

    [Test]
    public void NewPooledTransactionHashesMessageSerializer_should_match_canonical_rlp_vector_and_roundtrip()
    {
        NewPooledTransactionHashesMessageSerializer73 serializer = new();
        using NewPooledTransactionHashesMessage73 message = CreateCanonicalMessage();

        using DisposableByteBuffer serialized = PooledByteBufferAllocator.Default.Buffer().AsDisposable();
        serializer.Serialize(serialized, message);
        byte[] actualBytes = new byte[serialized.ReadableBytes];
        serialized.GetBytes(serialized.ReaderIndex, actualBytes);

        using NewPooledTransactionHashesMessage73 actual = serializer.Deserialize(serialized);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(actualBytes.ToHexString(), Is.EqualTo(CanonicalAnnouncementRlp));
            Assert.That(actual.Types, Is.EqualTo(message.Types));
            Assert.That(actual.Sizes, Is.EqualTo(message.Sizes));
            Assert.That(actual.Hashes, Is.EqualTo(message.Hashes));
            Assert.That(actual.CellMask, Is.EqualTo(message.CellMask));
            Assert.That(actual.Sources, Is.EqualTo(message.Sources));
            Assert.That(actual.Nonces, Is.EqualTo(message.Nonces));
        }
    }

    [TestCase("f85d820203c482040001f842a0000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1fa0202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f"
        + "9001000000000000000000000000000080", TestName = "eth/72 announcement without sources and nonces")]
    [TestCase("f877820203c482040001f842a0000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1fa0202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f"
        + "9001000000000000000000000000000080d5940102030405060708090a0b0c0d0e0f1011121314c3820102", TestName = "Fewer sources and nonces than hashes")]
    [TestCase("f88c820203c482040001f842a0000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1fa0202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f"
        + "9001000000000000000000000000000080e9940102030405060708090a0b0c0d0e0f101112131493ffffffffffffffffffffffffffffffffffffffc480820102", TestName = "Source shorter than 20 bytes")]
    [TestCase("f894820203c482040001f842a0000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1fa0202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f"
        + "9001000000000000000000000000000080ea940102030405060708090a0b0c0d0e0f101112131494ffffffffffffffffffffffffffffffffffffffffcb8089010000000000000000", TestName = "Nonce wider than 64 bits")]
    public void NewPooledTransactionHashesMessageSerializer_should_reject_malformed_announcement(string rlp)
    {
        NewPooledTransactionHashesMessageSerializer73 serializer = new();
        using DisposableByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer().AsDisposable();
        buffer.WriteBytes(Bytes.FromHexString(rlp));

        Assert.That(() => serializer.Deserialize(buffer), Throws.InstanceOf<RlpException>());
    }

    private static NewPooledTransactionHashesMessage73 CreateCanonicalMessage()
    {
        byte[] firstHash = new byte[32];
        byte[] secondHash = new byte[32];
        byte[] firstSource = new byte[20];
        for (int i = 0; i < 32; i++)
        {
            firstHash[i] = (byte)i;
            secondHash[i] = (byte)(i + 32);
        }

        for (int i = 0; i < 20; i++)
        {
            firstSource[i] = (byte)(i + 1);
        }

        return new NewPooledTransactionHashesMessage73(
            [(byte)TxType.EIP1559, (byte)TxType.Blob],
            [1024, 1],
            [new ValueHash256(firstHash), new ValueHash256(secondHash)],
            Convert.FromHexString("01000000000000000000000000000080"),
            [new Address(firstSource), new Address(Bytes.FromHexString("ffffffffffffffffffffffffffffffffffffffff"))],
            [0UL, 258UL]);
    }
}
