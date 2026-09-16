// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.State
{
    public partial class StorageTree
    {
        private const int LookupSize = 1024;

        /// <summary>Hashed trie keys for storage slots <c>0</c> to <see cref="LookupSize"/> - 1.</summary>
        /// <remarks>
        /// Built up front: a long-lived process amortises the cost over every block it goes on to
        /// process. See <c>StorageTree.zkevm.cs</c> for the guest form.
        /// </remarks>
        private static readonly ValueHash256[] Lookup = CreateLookup();

        private static ValueHash256[] CreateLookup()
        {
            Span<byte> buffer = stackalloc byte[32];
            ValueHash256[] lookup = new ValueHash256[LookupSize];

            for (int i = 0; i < lookup.Length; i++)
            {
                UInt256 index = new((uint)i);
                index.ToBigEndian(buffer);
                lookup[i] = ValueKeccak.Compute(buffer);
            }

            return lookup;
        }
    }
}
