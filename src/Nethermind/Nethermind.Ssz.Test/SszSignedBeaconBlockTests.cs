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
using Nethermind.Merkleization;
using NUnit.Framework;
using Shouldly;

namespace Nethermind.Ssz.Test
{
    [TestFixture]
    public class SszSignedBeaconBlockTests
    {
        [Test]
        public void CheckEmptyBlockRootAfterDeserializing()
        {
            // Arrange
            
            Eth1Data eth1Data = new Eth1Data(
                new Root(Enumerable.Repeat((byte)0x12, 32).ToArray()), 
                64,
                new Bytes32(Enumerable.Repeat((byte)0x34, 32).ToArray()));
            
            BlsSignature randaoReveal = new BlsSignature(Enumerable.Repeat((byte) 0xfe, 96).ToArray());

            BeaconBlockBody beaconBlockBody = new BeaconBlockBody(
                randaoReveal,
                eth1Data,
                new Bytes32(new byte[32]),
                new ProposerSlashing[0],
                new AttesterSlashing [0], 
                new Attestation[0],
                new Deposit[0],
                new SignedVoluntaryExit[0]
            );

            BeaconBlock beaconBlock = new BeaconBlock(
                new Slot(1),
                new Root(Enumerable.Repeat((byte)0x78, 32).ToArray()),
                new Root(Enumerable.Repeat((byte)0x9a, 32).ToArray()),
                beaconBlockBody);
            
            Merkle.Ize(out UInt256 blockRoot256, beaconBlock);
            Span<byte> blockRootSpan = MemoryMarshal.Cast<UInt256, byte>(MemoryMarshal.CreateSpan(ref blockRoot256, 1));
            Root blockRoot = new Root(blockRootSpan);
            
            SignedBeaconBlock signedBeaconBlock = new SignedBeaconBlock(
                beaconBlock,
                new BlsSignature(Enumerable.Repeat((byte)0x0e, 96).ToArray())
            );            
            
            // Act
            
            Span<byte> encoded = new byte[Ssz.SignedBeaconBlockLength(signedBeaconBlock)];
            Ssz.Encode(encoded, signedBeaconBlock);
            
            SignedBeaconBlock decoded = Ssz.DecodeSignedBeaconBlock(encoded);

            // Assert
            
            Merkle.Ize(out UInt256 decodedBlockRoot256, decoded.Message);
            Span<byte> decodedBlockRootSpan = MemoryMarshal.Cast<UInt256, byte>(MemoryMarshal.CreateSpan(ref decodedBlockRoot256, 1));
            Root decodedBlockRoot = new Root(decodedBlockRootSpan);

            decodedBlockRoot.ShouldBe(blockRoot);
        }
        
        [Test]
        public void CheckBlockWithDepositAfterDeserializing()
        {
            // Arrange
            
            Eth1Data eth1Data = new Eth1Data(
                new Root(Enumerable.Repeat((byte)0x12, 32).ToArray()), 
                64,
                new Bytes32(Enumerable.Repeat((byte)0x34, 32).ToArray()));
            
            Deposit deposit = new Deposit(
                Enumerable.Repeat(new Bytes32(Enumerable.Repeat((byte)0x11, 32).ToArray()), Ssz.DepositContractTreeDepth + 1), 
                new Ref<DepositData>(new DepositData(
                    new BlsPublicKey(Enumerable.Repeat((byte)0x22, 48).ToArray()), 
                    new Bytes32( Enumerable.Repeat((byte)0x33, 32).ToArray()),
                    new Gwei(32_000_000), 
                    new BlsSignature(Enumerable.Repeat((byte)0x44, 96).ToArray())
                    )));

            BlsSignature randaoReveal = new BlsSignature(Enumerable.Repeat((byte) 0xfe, 96).ToArray());

            BeaconBlockBody beaconBlockBody = new BeaconBlockBody(
                randaoReveal,
                eth1Data,
                new Bytes32(new byte[32]),
                new ProposerSlashing[0],
                new AttesterSlashing [0], 
                new Attestation[0],
                new Deposit[] {deposit},
                new SignedVoluntaryExit[0]
            );

            BeaconBlock beaconBlock = new BeaconBlock(
                new Slot(1),
                new Root(Enumerable.Repeat((byte)0x78, 32).ToArray()),
                new Root(Enumerable.Repeat((byte)0x9a, 32).ToArray()),
                beaconBlockBody);
            
            Merkle.Ize(out UInt256 blockRoot256, beaconBlock);
            Span<byte> blockRootSpan = MemoryMarshal.Cast<UInt256, byte>(MemoryMarshal.CreateSpan(ref blockRoot256, 1));
            Root blockRoot = new Root(blockRootSpan);
            
            SignedBeaconBlock signedBeaconBlock = new SignedBeaconBlock(
                beaconBlock,
                new BlsSignature(Enumerable.Repeat((byte)0x0e, 96).ToArray())
            );            
            
            // Act
            
            Span<byte> encoded = new byte[Ssz.SignedBeaconBlockLength(signedBeaconBlock)];
            Ssz.Encode(encoded, signedBeaconBlock);
            
            SignedBeaconBlock decoded = Ssz.DecodeSignedBeaconBlock(encoded);

            // Assert
            
            Merkle.Ize(out UInt256 decodedBlockRoot256, decoded.Message);
            Span<byte> decodedBlockRootSpan = MemoryMarshal.Cast<UInt256, byte>(MemoryMarshal.CreateSpan(ref decodedBlockRoot256, 1));
            Root decodedBlockRoot = new Root(decodedBlockRootSpan);

            decodedBlockRoot.ShouldBe(blockRoot);
        }

    }
}