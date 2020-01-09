//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System;
using System.Linq;
using System.Runtime.InteropServices;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using Nethermind.Dirichlet.Numerics;
using NUnit.Framework;
using Shouldly;

namespace Nethermind.Ssz.Test
{
    [TestFixture]
    public class HashTreeRootTests
    {
        [Test]
        public void Can_merkleize_epoch_0()
        {
            // arrange
            Epoch epoch = Epoch.Zero;

            // act
            Merkleizer merklezier = new Merkleizer(0);
            merklezier.Feed(epoch);
            UInt256 root = merklezier.CalculateRoot();
            Span<byte> bytes = MemoryMarshal.Cast<UInt256, byte>(MemoryMarshal.CreateSpan(ref root, 1));

            // assert
            byte[] expected = HashUtility.Chunk(new byte[] {0x0}).ToArray();
            bytes.ToArray().ShouldBe(expected);
        }
        
        [Test]
        public void Can_merkleize_epoch_1()
        {
            // arrange
            Epoch epoch = Epoch.One;

            // act
            Merkleizer merklezier = new Merkleizer(0);
            merklezier.Feed(epoch);
            UInt256 root = merklezier.CalculateRoot();
            Span<byte> bytes = MemoryMarshal.Cast<UInt256, byte>(MemoryMarshal.CreateSpan(ref root, 1));

            // assert
            byte[] expected = HashUtility.Chunk(new byte[] {0x0}).ToArray();
            bytes.ToArray().ShouldBe(expected);
        }

                
        [Test]
        public void Can_merkleize_hash32()
        {
            // arrange
            Hash32 hash32 = new Hash32(Enumerable.Repeat((byte) 0x34, 32).ToArray());

            // act
            Merkle.Ize(out UInt256 root, hash32);
            Span<byte> bytes = MemoryMarshal.Cast<UInt256, byte>(MemoryMarshal.CreateSpan(ref root, 1));

            // assert
            byte[] expected = Enumerable.Repeat((byte) 0x34, 32).ToArray();
            bytes.ToArray().ShouldBe(expected);
        }

        [Test]
        public void Can_merkleize_checkpoint()
        {
            // arrange
            Checkpoint checkpoint = new Checkpoint(
                new Epoch(3),
                new Hash32(Enumerable.Repeat((byte) 0x34, 32).ToArray())
            );

            // act
            Merkle.Ize(out UInt256 root, checkpoint);
            Span<byte> bytes = MemoryMarshal.Cast<UInt256, byte>(MemoryMarshal.CreateSpan(ref root, 1));

            // assert
            byte[] expected = HashUtility.Hash(
                HashUtility.Chunk(new byte[] {0x03}),
                Enumerable.Repeat((byte) 0x34, 32).ToArray()
            ).ToArray();
            bytes.ToArray().ShouldBe(expected);
        }

        [Test]
        public void Can_merkleize_attestion_data()
        {
            // arrange
            AttestationData attestationData = new AttestationData(
                Slot.One,
                new CommitteeIndex(2),
                new Hash32(Enumerable.Repeat((byte) 0x12, 32).ToArray()),
                new Checkpoint(
                    new Epoch(3),
                    new Hash32(Enumerable.Repeat((byte) 0x34, 32).ToArray())
                ),
                new Checkpoint(
                    new Epoch(4),
                    new Hash32(Enumerable.Repeat((byte) 0x56, 32).ToArray())
                )
            );

            // act
            Merkleizer merklezier = new Merkleizer(0);
            merklezier.Feed(attestationData);
            UInt256 root = merklezier.CalculateRoot();
            Span<byte> bytes = MemoryMarshal.Cast<UInt256, byte>(MemoryMarshal.CreateSpan(ref root, 1));

            // assert
            byte[] expected = HashUtility.Hash(
                HashUtility.Hash(
                    HashUtility.Hash(
                        HashUtility.Chunk(new byte[] {0x01}), // slot
                        HashUtility.Chunk(new byte[] {0x02}) // committee
                    ),
                    HashUtility.Hash(
                        Enumerable.Repeat((byte) 0x12, 32).ToArray(), // beacon block root
                        HashUtility.Hash( // source
                            HashUtility.Chunk(new byte[] {0x03}),
                            Enumerable.Repeat((byte) 0x34, 32).ToArray()
                        )
                    )
                ),
                HashUtility.Merge(
                    HashUtility.Hash( // target
                        HashUtility.Chunk(new byte[] {0x04}),
                        Enumerable.Repeat((byte) 0x56, 32).ToArray()
                    ),
                    HashUtility.ZeroHashes(0, 2)
                )
            ).ToArray();
            bytes.ToArray().ShouldBe(expected);
        }
    }
}