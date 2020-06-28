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
// 

using System;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using Nethermind.Core2.Types;
using Nethermind.Ssz;

namespace Nethermind.Merkleization
{
    public class ShaMerkleTree : MerkleTree
    {
        private static readonly Bytes32[] _zeroHashes = new Bytes32[32];
        private static readonly HashAlgorithm _hashAlgorithm = SHA256.Create();
        
        static ShaMerkleTree()
        {
            _zeroHashes[0] = new Bytes32();
            for (int index = 1; index < 32; index++)
            {
                _zeroHashes[index] = new Bytes32();
                HashStatic(_zeroHashes[index - 1].Unwrap(), _zeroHashes[index - 1].Unwrap(), _zeroHashes[index].Unwrap());
            }
        }
        
        public static ReadOnlyCollection<Bytes32> ZeroHashes => Array.AsReadOnly(_zeroHashes);

        public ShaMerkleTree(IKeyValueStore<ulong, byte[]> keyValueStore)
            : base(keyValueStore)
        {
        }
        
        public ShaMerkleTree() : base(new MemMerkleTreeStore())
        {
        }

        private static void HashStatic(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, Span<byte> target)
        {
            Span<byte> combined = stackalloc byte[a.Length + b.Length];
            a.CopyTo(combined);
            b.CopyTo(combined.Slice(a.Length));
            
            _hashAlgorithm.TryComputeHash(combined, target, out int bytesWritten);
        }

        protected override Bytes32[] ZeroHashesInternal => _zeroHashes;

        protected override void Hash(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, Span<byte> target)
        {
            HashStatic(a, b, target);
        }
    }
}