using System;
using System.Collections.Concurrent;
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

namespace Nethermind.State.Snap
{
    public class SnapProvider : ISnapProvider
    {
        private readonly ITrieStore _store;
        private readonly ILogManager _logManager;

        public Keccak NextAccountPath { get; set; } = Keccak.Zero; //new("0xfe00000000000000000000000000000000000000000000000000000000000000");
        public (PathWithAccount accountPath, Keccak nextSlotPath)? NextSlot { get; set; }
        public bool MoreAccountsToRight { get; set; } = true;
        public ConcurrentQueue<PathWithAccount> StoragesToRetrieve { get; private set; } = new();

        public SnapProvider(ITrieStore store, ILogManager logManager)
        {
            _store = store;
            _logManager = logManager;
        }


        public bool AddAccountRange(long blockNumber, Keccak expectedRootHash, Keccak startingHash, PathWithAccount[] accounts, byte[][] proofs = null)
        {
            StateTree tree = new(_store, _logManager);
            (Keccak? calculatedRootHash, bool moreChildrenToRight, IList<PathWithAccount> accountsWithStorage) = SnapProviderHelper.AddAccountRange(tree, blockNumber, expectedRootHash, startingHash, accounts, proofs);

            bool success = expectedRootHash == calculatedRootHash;

            if(success)
            {
                foreach (var item in accountsWithStorage)
                {
                    StoragesToRetrieve.Enqueue(item);
                }
                
                NextAccountPath = accounts[accounts.Length - 1].AddressHash;
                MoreAccountsToRight = moreChildrenToRight;
            }

            return success;
        }

        public bool AddStorageRange(long blockNumber, PathWithAccount pathWithAccount, Keccak expectedRootHash, Keccak startingHash, PathWithStorageSlot[] slots, byte[][] proofs = null)
        {
            StorageTree tree = new(_store, _logManager);
            (Keccak? calculatedRootHash, bool moreChildrenToRight) =  SnapProviderHelper.AddStorageRange(tree, blockNumber, expectedRootHash, startingHash, slots, proofs);

            bool success = expectedRootHash == calculatedRootHash;

            if (success)
            {
                if(moreChildrenToRight)
                {
                    NextSlot = (pathWithAccount, slots.Last().Path);
                }
            }

            return success;
        }
    }
}
