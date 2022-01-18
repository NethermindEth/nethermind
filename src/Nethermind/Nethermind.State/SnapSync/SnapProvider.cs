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
    public static class SnapProvider
    {
        public static Keccak? AddAccountRange(TrieStore store, long blockNumber, Keccak expectedRootHash, Keccak startingHash, AccountWithAddressHash[] accounts, byte[][] proofs = null)
        {
            StateTree tree = new(store, LimboLogs.Instance);
            return AddAccountRange(tree, blockNumber, expectedRootHash, startingHash, accounts, proofs);
        }

        public static Keccak? AddAccountRange(StateTree tree, long blockNumber, Keccak expectedRootHash, Keccak startingHash, AccountWithAddressHash[] accounts, byte[][] proofs = null)
        {
            // TODO: Check the accounts boundaries and sorting

            Keccak lastHash = accounts.Last().AddressHash;

            bool proved = ProcessProofs(tree, expectedRootHash, startingHash, lastHash, proofs);

            if (proved)
            {
                foreach (var account in accounts)
                {
                    tree.Set(account.AddressHash, account.Account);
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

        public static Keccak? AddStorageRange(TrieStore store, long blockNumber, Keccak expectedRootHash, Keccak startingHash, SlotWithKeyHash[] slots, byte[][] proofs = null)
        {
            StorageTree tree = new(store, LimboLogs.Instance);
            return AddStorageRange(tree, blockNumber, expectedRootHash, startingHash, slots, proofs);
        }

        public static Keccak? AddStorageRange(StorageTree tree, long blockNumber, Keccak expectedRootHash, Keccak startingHash, SlotWithKeyHash[] slots, byte[][] proofs = null)
        {
            // TODO: Check the slots boundaries and sorting

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

        private static bool ProcessProofs(PatriciaTree tree, Keccak expectedRootHash, Keccak startingHash, Keccak lastHash, byte[][] proofs = null)
        {
            if (proofs != null && proofs.Length > 0)
            {
                (bool proved, _) = ProofVerifier.VerifyMultipleProofs(proofs, expectedRootHash);

                if (!proved)
                {
                    //TODO: log incorrect proofs
                    return false;
                }

                SnapProviderHelper.FillBoundaryTree(tree, expectedRootHash, proofs, startingHash, lastHash);
            }

            return true;
        }

        
    }
}
