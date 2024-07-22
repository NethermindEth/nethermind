// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Linq;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Proofs
{
    public static class ProofVerifier
    {
        /// <summary>
        /// Verifies one proof - address path from the bottom to the root.
        /// </summary>
        /// <returns>The Value of the bottom most proof node. For example an Account.</returns>
        public static CappedArray<byte> VerifyOneProof(byte[][] proof, Hash256 root)
        {
            if (proof.Length == 0)
            {
                return null;
            }

            for (int i = proof.Length; i > 0; i--)
            {
                Hash256 proofHash = Keccak.Compute(proof[i - 1]);
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
            trieNode.ResolveNode(null, TreePath.Empty);

            return trieNode.Value;
        }
    }
}
