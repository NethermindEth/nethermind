// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;

namespace Nethermind.State.Proofs
{
    public class TxTrie : PatriciaTree
    {
        private readonly bool _allowMerkleProofConstructions;
        private static readonly TxDecoder _txDecoder = new();

        /// <summary>
        /// Helper class used for calculation of tx roots for block headers.
        /// </summary>
        /// <param name="txs">Transactions to build a trie from.</param>
        /// <param name="allowMerkleProofConstructions">Some tries do not need to be used for proof constructions.
        /// In such cases we can avoid maintaining any in-memory databases.</param>
        public TxTrie(IReadOnlyList<Transaction>? txs, bool allowMerkleProofConstructions = false)
            : base(allowMerkleProofConstructions ? (IDb)new MemDb() : NullDb.Instance, EmptyTreeHash, false, false, NullLogManager.Instance)
        {
            _allowMerkleProofConstructions = allowMerkleProofConstructions;
            if ((txs?.Count ?? 0) == 0)
            {
                return;
            }

            // 3% allocations (2GB) on a Goerli 3M blocks fast sync due to calling transaction encoder here
            // Avoiding it would require pooling byte arrays and passing them as Spans to temporary trees
            // a temporary trie would be a trie that exists to create a state root only and then be disposed of
            for (int i = 0; i < txs.Count; i++)
            {
                Rlp transactionRlp = _txDecoder.Encode(txs[i], RlpBehaviors.SkipTypedWrapping);
                Set(Rlp.Encode(i).Bytes, transactionRlp.Bytes);
            }

            // additional 3% 2GB is used here for trie nodes creation and root calculation
            UpdateRootHash();
        }

        public byte[][] BuildProof(int index)
        {
            if (!_allowMerkleProofConstructions)
            {
                throw new InvalidOperationException("Cannot build proofs without underlying DB (for now?)");
            }

            ProofCollector proofCollector = new(Rlp.Encode(index).Bytes);
            Accept(proofCollector, RootHash, new VisitingOptions { ExpectAccounts = false });
            return proofCollector.BuildResult();
        }
    }
}
