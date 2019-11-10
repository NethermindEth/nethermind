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
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using Nethermind.Core;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using Nethermind.Dirichlet.Numerics;
using NLog.StructuredLogging.Json.Helpers;
using Chunk = Nethermind.Dirichlet.Numerics.UInt256;

namespace Nethermind.Ssz
{
    public static partial class Merkle
    {
        public static void Ize(Span<byte> root, BlsPublicKey container)
        {
            Ize(root, container.Bytes);
        }
        
        public static void Ize(Span<byte> root, BlsSignature container)
        {
            Ize(root, container.Bytes);
        }

        public static void Ize(Span<byte> root, Gwei container)
        {
            Ize(root, container.Amount);
        }
        
        public static void Ize(Span<byte> root, Slot container)
        {
            Ize(root, container.Number);
        }
        
        public static void Ize(Span<byte> root, Epoch container)
        {
            Ize(root, container.Number);
        }
        
        public static void Ize(Span<byte> root, ValidatorIndex container)
        {
            Ize(root, container.Number);
        }
        
        public static void Ize(Span<byte> root, CommitteeIndex container)
        {
            Ize(root, container.Number);
        }
        
        public static void Ize(Span<byte> root, Eth1Data container)
        {
            Merkleizer merkleizer = new Merkleizer(2);
            merkleizer.Feed(container.DepositRoot);
            merkleizer.Feed(container.DepositCount);
            merkleizer.Feed(container.BlockHash);
            merkleizer.CalculateRoot(root);
        }
        
        public static void Ize(Span<byte> root, VoluntaryExit container)
        {
            Merkleizer merkleizer = new Merkleizer(2);
            merkleizer.Feed(container.Epoch);
            merkleizer.Feed(container.ValidatorIndex);
            merkleizer.Feed(container.Signature);
            merkleizer.CalculateRoot(root);
        }
        
        public static void Ize(Span<byte> root, Validator container)
        {
            Span<Chunk> chunks = new Chunk[8];
            Span<byte> partRoot = new byte[32];
            Ize(partRoot, container.PublicKey);
            chunks[0] = MemoryMarshal.Read<Chunk>(partRoot);
            partRoot = new byte[32];
            Ize(partRoot, container.WithdrawalCredentials);
            chunks[1] = MemoryMarshal.Read<Chunk>(partRoot);
            partRoot = new byte[32];
            Ize(partRoot, container.EffectiveBalance);
            chunks[2] = MemoryMarshal.Read<Chunk>(partRoot);
            partRoot = new byte[32];
            Ize(partRoot, container.Slashed);
            chunks[3] = MemoryMarshal.Read<Chunk>(partRoot);
            partRoot = new byte[32];
            Ize(partRoot, container.ActivationEligibilityEpoch);
            chunks[4] = MemoryMarshal.Read<Chunk>(partRoot);
            partRoot = new byte[32];
            Ize(partRoot, container.ActivationEpoch);
            chunks[5] = MemoryMarshal.Read<Chunk>(partRoot);
            partRoot = new byte[32];
            Ize(partRoot, container.ExitEpoch);
            chunks[6] = MemoryMarshal.Read<Chunk>(partRoot);
            partRoot = new byte[32];
            Ize(partRoot, container.WithdrawableEpoch);
            chunks[7] = MemoryMarshal.Read<Chunk>(partRoot);
            Ize(root, chunks, Span<Chunk>.Empty);
        }
    }
}