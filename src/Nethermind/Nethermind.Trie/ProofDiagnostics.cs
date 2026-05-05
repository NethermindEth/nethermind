// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Trie
{
    public sealed class ProofDiagnostics
    {
        public long NodeLookups { get; private set; }
        public long CacheMisses { get; private set; }
        public int MaxDepth { get; private set; }

        public long CacheHits => NodeLookups - CacheMisses < 0 ? 0 : NodeLookups - CacheMisses;

        public void RecordLookup() => NodeLookups++;

        public void RecordCacheMiss() => CacheMisses++;

        public void ObserveDepth(int level)
        {
            if (level > MaxDepth) MaxDepth = level;
        }
    }
}
