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
using Nethermind.Trie;

namespace Nethermind.Baseline
{
    public class ShaMerkleTree : MerkleTree
    {
        private static readonly byte[][] _zeroHashes = new byte[32][];
        private static readonly HashAlgorithm _hashAlgorithm = SHA256.Create();
        
        static ShaMerkleTree()
        {
            _zeroHashes[0] = new byte[32];
            for (int index = 1; index < 32; index++)
            {
                _zeroHashes[index] = HashStatic(_zeroHashes[index - 1], _zeroHashes[index - 1]);
            }
        }
        
        /// <summary>
        /// Zero hashes are always 32 bytes long (not truncated)
        /// </summary>
        public static ReadOnlyCollection<byte[]> ZeroHashes => Array.AsReadOnly(_zeroHashes);

        public ShaMerkleTree(IKeyValueStore keyValueStore, int truncationLength = 0)
            : base(keyValueStore, truncationLength)
        {
            
        }

        private static byte[] HashStatic(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        {
            Span<byte> combined = new Span<byte>(new byte[a.Length + b.Length]);
            a.CopyTo(combined);
            b.CopyTo(combined.Slice(a.Length));
            
            // TryComputeHash here?
            return _hashAlgorithm.ComputeHash(combined.ToArray());
        }

        protected override byte[][] ZeroHashesInternal => _zeroHashes;

        protected override byte[] Hash(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        {
            return HashStatic(a.Slice(TruncationLength, 32 - TruncationLength), b.Slice(TruncationLength, 32 - TruncationLength));
        }
    }
}