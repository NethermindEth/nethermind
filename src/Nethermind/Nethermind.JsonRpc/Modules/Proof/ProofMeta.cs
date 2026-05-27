// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.JsonRpc.Modules.Proof
{
    /// <summary>
    /// Per-call diagnostics returned by <c>proof_getProofWithMeta</c> alongside the EIP-1186 proof.
    /// </summary>
    public class ProofMeta
    {
        /// <summary>
        /// Total trie-node fetches the proof construction triggered (account + any storage tries).
        /// </summary>
        public long NodeLookups { get; set; }

        /// <summary>
        /// Derived count: <see cref="NodeLookups"/> minus cache-miss fetches, clamped at zero.
        /// Represents nodes served from the in-process trie store cache under the normal proof
        /// code path where every cache miss is preceded by exactly one lookup.
        /// </summary>
        public long CacheHits { get; set; }

        /// <summary>
        /// Deepest level the visitor reached in the account or any storage trie, in nibbles.
        /// </summary>
        public long MaxDepth { get; set; }
    }
}
