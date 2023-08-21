// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.Linq;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;

namespace Nethermind.State.Proofs
{
    public static class ProofVerifier
    {
        /// <summary>
        /// Verifies one proof - address path from the bottom to the root.
        /// </summary>
        /// <returns>The Value of the bottom most proof node. For example an Account.</returns>
        public static byte[]? VerifyOneProof(byte[][] proof, Keccak root)
        {
            if (proof.Length == 0)
            {
                return null;
            }

            for (int i = proof.Length; i > 0; i--)
            {
                Keccak proofHash = Keccak.Compute(proof[i - 1]);
                if (i > 1)
                {
                    if (!new Rlp(proof[i - 2]).ToString(false).Contains(proofHash.ToString(false)))
                    {
                        throw new InvalidDataException();
                    }
                }
                else
                {
                    if (proofHash != root)
                    {
                        throw new InvalidDataException();
                    }
                }
            }

            TrieNode trieNode = new(NodeType.Unknown, proof.Last());
            trieNode.ResolveNode(null);

            return trieNode.Value.ToArrayOrNull();
        }
    }
}
