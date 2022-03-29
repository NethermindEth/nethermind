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
        private readonly ILogger _logger;

        public ProgressTracker ProgressTracker { get; set; }

        public SnapProvider(ITrieStore store, ILogManager logManager)
        {
            _store = store;
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _logger = logManager.GetClassLogger();
            ProgressTracker = new(_logManager);
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
                    ProgressTracker.EnqueueAccountStorage(item);
                }

                ProgressTracker.NextAccountPath = accounts[accounts.Length - 1].AddressHash;
                ProgressTracker.MoreAccountsToRight = moreChildrenToRight;
            }
            else
            {
                _store.Prune();
                _logger.Warn($"SNAP - AddAccountRange failed, {blockNumber}:{expectedRootHash}, startingHash:{startingHash}");
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
                    StorageRange range = new()
                    {
                        Accounts = new[] { pathWithAccount },
                        StartingHash = slots.Last().Path
                    };

                    ProgressTracker.EnqueueAccountStorage(range);
                }
            }
            else
            {
                _store.Prune();
                _logger.Warn($"SNAP - AddStorageRange failed, {blockNumber}:{expectedRootHash}, startingHash:{startingHash}");

                if (startingHash > Keccak.Zero)
                {
                    StorageRange range = new()
                    {
                        Accounts = new[] { pathWithAccount },
                        StartingHash = startingHash
                    };

                    ProgressTracker.EnqueueAccountStorage(range);
                }
                else
                {
                    ProgressTracker.EnqueueAccountStorage(pathWithAccount);
                }
            }

            return success;
        }
    }
}
