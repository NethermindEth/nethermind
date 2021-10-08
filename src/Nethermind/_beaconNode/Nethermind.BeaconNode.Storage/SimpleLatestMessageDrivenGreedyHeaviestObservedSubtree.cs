//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nethermind.Core2;
using Nethermind.Core2.Configuration;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Store;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Storage
{
    public class SimpleLatestMessageDrivenGreedyHeaviestObservedSubtree : IHeadSelectionStrategy
    {
        // See: https://blog.ethereum.org/2020/02/12/validated-staking-on-eth2-2-two-ghosts-in-a-trench-coat/

        private readonly IBeaconChainUtility _beaconChainUtility;
        private readonly BeaconStateAccessor _beaconStateAccessor;
        private readonly ChainConstants _chainConstants;
        private readonly ILogger _logger;

        public SimpleLatestMessageDrivenGreedyHeaviestObservedSubtree(
            ILogger<SimpleLatestMessageDrivenGreedyHeaviestObservedSubtree> logger,
            ChainConstants chainConstants,
            IBeaconChainUtility beaconChainUtility,
            BeaconStateAccessor beaconStateAccessor)
        {
            _logger = logger;
            _chainConstants = chainConstants;
            _beaconChainUtility = beaconChainUtility;
            _beaconStateAccessor = beaconStateAccessor;
        }

        public async Task<bool> FilterBlockTreeAsync(IStore store, Root blockRoot,
            IDictionary<Root, BeaconBlock> blocks)
        {
            SignedBeaconBlock signedBeaconBlock = await store.GetSignedBlockAsync(blockRoot).ConfigureAwait(false);

            // If any children branches contain expected finalized/justified checkpoints,
            // add to filtered block-tree and signal viability to parent.
            bool hasChildren = false;
            bool anyChildResult = false;
            await foreach (Root childKey in store.GetChildKeysAsync(blockRoot)
                .ConfigureAwait(false))
            {
                hasChildren = true;
                bool childResult = await FilterBlockTreeAsync(store, childKey, blocks).ConfigureAwait(false);
                anyChildResult = anyChildResult | childResult;
            }

            if (hasChildren)
            {
                if (anyChildResult)
                {
                    blocks[blockRoot] = signedBeaconBlock.Message;
                }

                return anyChildResult;
            }

            // If leaf block, check finalized/justified checkpoints as matching latest.
            BeaconState headState = await store.GetBlockStateAsync(blockRoot).ConfigureAwait(false);

            bool correctJustified = store.JustifiedCheckpoint.Epoch == _chainConstants.GenesisEpoch
                                    || headState.CurrentJustifiedCheckpoint == store.JustifiedCheckpoint;
            bool correctFinalized = store.FinalizedCheckpoint.Epoch == _chainConstants.GenesisEpoch
                                    || headState.FinalizedCheckpoint == store.FinalizedCheckpoint;

            // If expected finalized/justified, add to viable block-tree and signal viability to parent.
            if (correctJustified && correctFinalized)
            {
                blocks[blockRoot] = signedBeaconBlock.Message;
                return true;
            }

            // Otherwise, branch not viable
            return false;
        }

        /// <summary>
        /// Retrieve a filtered block tree from ``store``, only returning branches
        /// whose leaf state's justified/finalized info agrees with that in ``store``. 
        /// </summary>
        public async Task<IDictionary<Root, BeaconBlock>> GetFilteredBlockTreeAsync(IStore store)
        {
            Root baseRoot = store.JustifiedCheckpoint.Root;
            IDictionary<Root, BeaconBlock> blocks = new Dictionary<Root, BeaconBlock>();
            _ = await FilterBlockTreeAsync(store, baseRoot, blocks);
            return blocks;
        }

        public async Task<Root> GetHeadAsync(IStore store)
        {
            // NOTE: These functions have been moved here, instead of ForkChoice, because some of them may benefit
            // from direct looking in the storage mechanism (e.g. database) or have other ways to optimise based on
            // the actual storage used.

            // TODO: Several different implementations provided in spec, for efficiency
            // TODO: Also, should cache, i.e. will only change if store is updated (so should be easy to cache if in store)

            // Get filtered block tree that only includes viable branches
            IDictionary<Root, BeaconBlock> blocks = await GetFilteredBlockTreeAsync(store).ConfigureAwait(false);

            // Latest Message Driven - Greedy Heaviest Observed Subtree
            // Fresh Message Driven

            // Execute the LMD-GHOST fork choice
            Root head = store.JustifiedCheckpoint.Root;
            Slot justifiedSlot = _beaconChainUtility.ComputeStartSlotOfEpoch(store.JustifiedCheckpoint.Epoch);
            while (true)
            {
                List<Tuple<Root, Gwei>> childKeysWithBalances = new List<Tuple<Root, Gwei>>();
                foreach (KeyValuePair<Root, BeaconBlock> kvp in blocks)
                {
                    if (kvp.Value.ParentRoot.Equals(head) && kvp.Value.Slot > justifiedSlot)
                    {
                        Gwei balance = await GetLatestAttestingBalanceAsync(store, kvp.Key).ConfigureAwait(false);
                        childKeysWithBalances.Add(Tuple.Create(kvp.Key, balance));
                    }
                }

                if (childKeysWithBalances.Count == 0)
                {
                    return head;
                }

                // Sort by latest attesting balance with ties broken lexicographically
                head = childKeysWithBalances
                    .OrderByDescending(x => x.Item2)
                    .ThenByDescending(x => x.Item1)
                    .Select(x => x.Item1)
                    .First();
            }
        }

        public async Task<Gwei> GetLatestAttestingBalanceAsync(IStore store, Root root)
        {
            Checkpoint justifiedCheckpoint = store.JustifiedCheckpoint;
            BeaconState state = (await store.GetCheckpointStateAsync(justifiedCheckpoint, true).ConfigureAwait(false))!;
            Epoch currentEpoch = _beaconStateAccessor.GetCurrentEpoch(state);
            IList<ValidatorIndex> activeIndexes = _beaconStateAccessor.GetActiveValidatorIndices(state, currentEpoch);
            SignedBeaconBlock rootBlock = await store.GetSignedBlockAsync(root).ConfigureAwait(false);

            Slot rootSlot = rootBlock!.Message.Slot;
            Gwei balance = Gwei.Zero;
            foreach (ValidatorIndex index in activeIndexes)
            {
                LatestMessage? latestMessage = await store.GetLatestMessageAsync(index, false);
                if (latestMessage != null)
                {
                    Root ancestor = await store.GetAncestorAsync(latestMessage.Root, rootSlot);
                    if (ancestor == root)
                    {
                        Validator validator = state.Validators[(int) index];
                        balance += validator.EffectiveBalance;
                    }
                }
            }

            return balance;
        }
    }
}