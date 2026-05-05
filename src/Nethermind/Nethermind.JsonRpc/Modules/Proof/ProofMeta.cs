// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.JsonRpc.Modules.Proof
{
    public class ProofMeta
    {
        public long NodeLookups { get; set; }
        public long CacheHits { get; set; }
        public int MaxDepth { get; set; }
    }
}
