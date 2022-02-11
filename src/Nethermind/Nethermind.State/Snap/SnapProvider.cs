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

namespace Nethermind.State.Snap
{
    public class SnapProvider
    {
        private readonly ITrieStore _store;
        private readonly ILogManager _logManager;

        public Keccak NextStartingHash { get; private set; } = Keccak.Zero;

        public SnapProvider(ITrieStore store, ILogManager logManager)
        {
            _store = store;
            _logManager = logManager;
        }


        public bool AddAccountRange(long blockNumber, Keccak expectedRootHash, Keccak startingHash, PathWithAccount[] accounts, byte[][] proofs = null)
        {
            StateTree tree = new(_store, _logManager);
            Keccak calculatedRootHash = SnapProviderHelper.AddAccountRange(tree, blockNumber, expectedRootHash, startingHash, accounts, proofs);

            bool success = expectedRootHash == calculatedRootHash;

            if(success)
            {
                NextStartingHash = accounts[accounts.Length - 1].AddressHash;
            }

            return success;
        }

        public Keccak? AddStorageRange(long blockNumber, Keccak expectedRootHash, Keccak startingHash, SlotWithKeyHash[] slots, byte[][] proofs = null)
        {
            StorageTree tree = new(_store, _logManager);
            return SnapProviderHelper.AddStorageRange(tree, blockNumber, expectedRootHash, startingHash, slots, proofs);
        }
    }
}
