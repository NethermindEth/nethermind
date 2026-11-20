// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Eth.V74.Messages;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V74;

[Parallelizable(ParallelScope.All)]
public class DependencyProofsMessageSerializerTests
{
    // EIP-8498: an unavailable proof and the empty proof of a block without dependencies both encode as 0x80.
    [TestCase(new byte[0], "c4" + "2a" + "c280" + "80", TestName = "Empty_proof_is_0x80")]
    [TestCase(new byte[] { 0x01 }, "c4" + "2a" + "c201" + "80", TestName = "Single_byte_proof")]
    [TestCase(new byte[] { 0xaa, 0xbb }, "c6" + "2a" + "c4" + "82aabb" + "80", TestName = "Short_proof")]
    public void Roundtrip(byte[] proof, string expectedHex)
    {
        using DependencyProofsMessage message = new(42, new ArrayPoolList<byte[]>(2, new[] { proof, [] }));
        DependencyProofsMessageSerializer serializer = new();
        using DisposableByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer(64).AsDisposable();

        serializer.Serialize(buffer, message);
        string hex = buffer.ReadAllHex();
        buffer.SetReaderIndex(0);
        using DependencyProofsMessage decoded = serializer.Deserialize(buffer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(hex, Is.EqualTo(expectedHex));
            Assert.That(decoded.RequestId, Is.EqualTo(42));
            Assert.That(decoded.Proofs.ToArray(), Is.EqualTo(new[] { proof, [] }));
            Assert.That(Keccak.Compute(decoded.Proofs[1]), Is.EqualTo(Keccak.OfAnEmptyString),
                "the empty entry hashes to the commitment of a block without dependencies");
        }
    }

    [Test]
    public void Rejects_a_proof_over_the_local_bound()
    {
        using DependencyProofsMessage message = new(1, new ArrayPoolList<byte[]>(1) { new byte[Eip8288Constants.MaxAggregateProofBytes + 1] });
        DependencyProofsMessageSerializer serializer = new();
        using DisposableByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer(Eip8288Constants.MaxAggregateProofBytes + 16).AsDisposable();
        serializer.Serialize(buffer, message);

        Assert.Throws<RlpLimitException>(() => serializer.Deserialize(buffer));
    }

    [Test]
    public void Get_request_roundtrips()
    {
        using GetDependencyProofsMessage message = new(7, new ArrayPoolList<Hash256>(2) { TestItem.KeccakA, TestItem.KeccakB });
        GetDependencyProofsMessageSerializer serializer = new();
        using DisposableByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer(128).AsDisposable();

        serializer.Serialize(buffer, message);
        using GetDependencyProofsMessage decoded = serializer.Deserialize(buffer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.RequestId, Is.EqualTo(7));
            Assert.That(decoded.Hashes.ToArray(), Is.EqualTo(new[] { TestItem.KeccakA, TestItem.KeccakB }));
        }
    }
}
