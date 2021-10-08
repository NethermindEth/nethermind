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
using System.Security.Cryptography;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie;

namespace Nethermind.Baseline.Tree
{
    public class ShaBaselineTree : BaselineTree
    {
        [ThreadStatic] private static HashAlgorithm? _hashAlgorithm;

        public ShaBaselineTree(IDb db, IKeyValueStore metadataKeyValueStore, byte[] dbPrefix, int truncationLength, ILogger logger)
            : base(db, metadataKeyValueStore, dbPrefix, truncationLength, logger)
        {
        }

        private static void InitHashIfNeeded()
        {
            _hashAlgorithm ??= SHA256.Create();
        }

        private static void HashStatic(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, Span<byte> target)
        {
            Span<byte> combined = new Span<byte>(new byte[a.Length + b.Length]);
            a.CopyTo(combined);
            b.CopyTo(combined.Slice(a.Length));

            InitHashIfNeeded();
            _hashAlgorithm!.TryComputeHash(combined, target, out _);
        }

        protected override void Hash(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, Span<byte> target)
        {
            HashStatic(
                a.Slice(TruncationLength, 32 - TruncationLength),
                b.Slice(TruncationLength, 32 - TruncationLength),
                target);
        }
    }
}
