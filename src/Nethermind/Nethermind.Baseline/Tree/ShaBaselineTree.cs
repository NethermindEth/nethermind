// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
