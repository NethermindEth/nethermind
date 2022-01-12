using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Proofs;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.SnapSync
{
    public class SnapProvider
    {
        private readonly TrieStore _store;

        public SnapProvider(TrieStore store)
        {;
            _store = store;
        }

        public Keccak? AddAccountRange(long blockNumber, Keccak expectedRootHash, Keccak startingHash, AccountWithAddressHash[] accounts, byte[][] proofs = null)
        {
            // TODO: Check the accounts boundaries and sorting

            StateTree tree = new StateTree(_store, LimboLogs.Instance);
            Keccak lastHash = accounts.Last().AddressHash;

            bool proved = ProcessProofs(tree, expectedRootHash, startingHash, lastHash, proofs);

            if (proved)
            {
                foreach (var account in accounts)
                {
                    tree.Set(account.AddressHash, account.Account);
                }

                tree.UpdateRootHash();

                if(tree.RootHash != expectedRootHash)
                {
                    // TODO: log incorrect range
                    return Keccak.EmptyTreeHash;
                }

                tree.Commit(blockNumber);
            }

            return tree.RootHash;
        }

        public Keccak? AddStorageRange(long blockNumber, Keccak expectedRootHash, Keccak startingHash, SlotWithKeyHash[] slots, byte[][] proofs = null)
        {
            // TODO: Check the slots boundaries and sorting

            StorageTree tree = new(_store, LimboLogs.Instance);
            Keccak lastHash = slots.Last().KeyHash;

            bool proved = ProcessProofs(tree, expectedRootHash, startingHash, lastHash, proofs);

            if (proved)
            {
                foreach (var slot in slots)
                {
                    tree.Set(slot.KeyHash, slot.SlotValue);
                }

                tree.UpdateRootHash();

                if (tree.RootHash != expectedRootHash)
                {
                    // TODO: log incorrect range
                    return Keccak.EmptyTreeHash;
                }

                tree.Commit(blockNumber);
            }

            return tree.RootHash;
        }

        private bool ProcessProofs(PatriciaTree tree, Keccak expectedRootHash, Keccak startingHash, Keccak lastHash, byte[][] proofs = null)
        {
            if (proofs != null && proofs.Length > 0)
            {
                (bool proved, _) = ProofVerifier.VerifyMultipleProofs(proofs, expectedRootHash);

                if (!proved)
                {
                    //TODO: log incorrect proofs
                    return false;
                }

                SnapProviderHelper.FillBoundaryTree(_store, tree, expectedRootHash, proofs, startingHash, lastHash);
            }

            return true;
        }

        
    }
}
