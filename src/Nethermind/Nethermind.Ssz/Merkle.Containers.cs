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
using Nethermind.Dirichlet.Numerics;
using NLog.StructuredLogging.Json.Helpers;
using Chunk = Nethermind.Dirichlet.Numerics.UInt256;

namespace Nethermind.Ssz
{
    public static partial class Merkle
    {
        public static void Ize(Span<byte> root, Eth1Data ethData)
        {
            Span<byte> depositCountRoot = new byte[32];
            Ize(depositCountRoot, ethData.DepositCount);

            Span<Chunk> chunks = new Chunk[3];
            chunks[0] = MemoryMarshal.Read<Chunk>(ethData.DepositRoot.Bytes);
            chunks[1] = MemoryMarshal.Read<Chunk>(depositCountRoot);
            chunks[2] = MemoryMarshal.Read<Chunk>(ethData.BlockHash.Bytes);

            Ize(root, chunks, Span<Chunk>.Empty);
        }
    }
}