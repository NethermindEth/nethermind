using System;
using System.Collections.Generic;
using Cortex.BeaconNode.Configuration;
using Cortex.BeaconNode.Data;
using Cortex.BeaconNode.Ssz;
using Cortex.Containers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cortex.BeaconNode
{
    public class ForkChoice
    {
        private readonly BeaconChainUtility _beaconChainUtility;
        private readonly BeaconStateTransition _beaconStateTransition;
        private readonly IOptionsMonitor<InitialValues> _initialValueOptions;
        private readonly ILogger _logger;
        private readonly IOptionsMonitor<MaxOperationsPerBlock> _maxOperationsPerBlockOptions;
        private readonly IOptionsMonitor<ForkChoiceConfiguration> _forkChoiceConfigurationOptions;
        private readonly IOptionsMonitor<MiscellaneousParameters> _miscellaneousParameterOptions;
        private readonly IOptionsMonitor<StateListLengths> _stateListLengthOptions;
        private readonly IStoreProvider _storeProvider;
        private readonly IOptionsMonitor<TimeParameters> _timeParameterOptions;

        public ForkChoice(
            ILogger<ForkChoice> logger,
            IOptionsMonitor<MiscellaneousParameters> miscellaneousParameterOptions,
            IOptionsMonitor<InitialValues> initialValueOptions,
            IOptionsMonitor<TimeParameters> timeParameterOptions,
            IOptionsMonitor<StateListLengths> stateListLengthOptions,
            IOptionsMonitor<MaxOperationsPerBlock> maxOperationsPerBlockOptions,
            IOptionsMonitor<ForkChoiceConfiguration> forkChoiceConfigurationOptions,
            BeaconChainUtility beaconChainUtility,
            BeaconStateTransition beaconStateTransition,
            IStoreProvider storeProvider)
        {
            _logger = logger;
            _miscellaneousParameterOptions = miscellaneousParameterOptions;
            _initialValueOptions = initialValueOptions;
            _timeParameterOptions = timeParameterOptions;
            _stateListLengthOptions = stateListLengthOptions;
            _maxOperationsPerBlockOptions = maxOperationsPerBlockOptions;
            _forkChoiceConfigurationOptions = forkChoiceConfigurationOptions;
            _beaconChainUtility = beaconChainUtility;
            _beaconStateTransition = beaconStateTransition;
            _storeProvider = storeProvider;
        }

        public Slot ComputeSlotsSinceEpochStart(Slot slot)
        {
            var epoch = _beaconChainUtility.ComputeEpochAtSlot(slot);
            var startSlot = _beaconChainUtility.ComputeStartSlotOfEpoch(epoch);
            return slot - startSlot;
        }

        public Hash32 GetAncestor(IStore store, Hash32 root, Slot slot)
        {
            // NOTE: This method should probably live in IStore, for various efficient implementations.

            if (!store.TryGetBlock(root, out var block))
            {
                throw new Exception($"Block not found for get ancestor root {root}.");
            }
            if (block.Slot > slot)
            {
                return GetAncestor(store, block.ParentRoot, slot);
            }
            else if (block.Slot == slot)
            {
                return root;
            }
            else
            {
                // root is older than queried slot: no results.
                return Hash32.Zero;
            }
        }

        public Slot GetCurrentSlot(IStore store)
        {
            var slotValue = (store.Time - store.GenesisTime) / _timeParameterOptions.CurrentValue.SecondsPerSlot;
            return new Slot(slotValue);
        }

        public IStore GetGenesisStore(BeaconState genesisState)
        {
            var miscellaneousParameters = _miscellaneousParameterOptions.CurrentValue;
            var maxOperationsPerBlock = _maxOperationsPerBlockOptions.CurrentValue;

            var stateRoot = genesisState.HashTreeRoot(miscellaneousParameters, _timeParameterOptions.CurrentValue, _stateListLengthOptions.CurrentValue, maxOperationsPerBlock);
            var genesisBlock = new BeaconBlock(stateRoot);
            var root = genesisBlock.SigningRoot(miscellaneousParameters, maxOperationsPerBlock);
            var justifiedCheckpoint = new Checkpoint(_initialValueOptions.CurrentValue.GenesisEpoch, root);
            var finalizedCheckpoint = new Checkpoint(_initialValueOptions.CurrentValue.GenesisEpoch, root);

            _logger.LogInformation(Event.CreateGenesisStore, "Creating genesis store with block {BeaconBlock} for state {BeaconState}, with checkpoint {JustifiedCheckpoint}, with signing root {SigningRoot}",
                genesisBlock, genesisState, justifiedCheckpoint, root);

            var blocks = new Dictionary<Hash32, BeaconBlock>
            {
                [root] = genesisBlock
            };
            var blockStates = new Dictionary<Hash32, BeaconState>
            {
                [root] = BeaconState.Clone(genesisState)
            };
            var checkpointStates = new Dictionary<Checkpoint, BeaconState>
            {
                [justifiedCheckpoint] = BeaconState.Clone(genesisState)
            };

            var store = _storeProvider.CreateStore(
                genesisState.GenesisTime,
                genesisState.GenesisTime,
                justifiedCheckpoint,
                finalizedCheckpoint,
                justifiedCheckpoint,
                blocks,
                blockStates,
                checkpointStates
                );
            return store;
        }

        public void OnBlock(IStore store, BeaconBlock block)
        {
            // Make a copy of the state to avoid mutability issues
            if (!store.TryGetBlockState(block.ParentRoot, out var parentState))
            {
                throw new ArgumentOutOfRangeException(nameof(block), block.ParentRoot, "Block parent root not found in the block states history.");
            }
            var preState = BeaconState.Clone(parentState);

            // Blocks cannot be in the future. If they are, their consideration must be delayed until the are in the past.
            var blockTime = preState.GenesisTime + (ulong)block.Slot * _timeParameterOptions.CurrentValue.SecondsPerSlot;
            if (blockTime > store.Time)
            {
                throw new ArgumentOutOfRangeException(nameof(block), blockTime, $"Block slot time cannot be in the future, compared to store time {store.Time}.");
            }

            // Add new block to the store
            var signingRoot = block.SigningRoot(_miscellaneousParameterOptions.CurrentValue, _maxOperationsPerBlockOptions.CurrentValue);
            store.AddBlock(signingRoot, block);

            // Check block is a descendant of the finalized block
            if (!store.TryGetBlock(store.FinalizedCheckpoint.Root, out var finalizedCheckpointBlock))
            {
                throw new Exception($"Block not found for finalized checkpoint root {store.FinalizedCheckpoint.Root}.");
            }
            var ancestor = GetAncestor(store, signingRoot, finalizedCheckpointBlock.Slot);
            if (ancestor != store.FinalizedCheckpoint.Root)
            {
                throw new Exception($"Block with signing root {signingRoot} is not a descendant of the finalized block {store.FinalizedCheckpoint.Root} at slot {finalizedCheckpointBlock.Slot}.");
            }

            // Check that block is later than the finalized epoch slot
            var finalizedEpochStartSlot = _beaconChainUtility.ComputeStartSlotOfEpoch(store.FinalizedCheckpoint.Epoch);
            if (block.Slot <= finalizedEpochStartSlot)
            {
                throw new ArgumentOutOfRangeException(nameof(block), block.Slot, $"Block slot must be later than the finalized epoch start slot {finalizedEpochStartSlot}.");
            }

            // Check the block is valid and compute the post-state
            var state = _beaconStateTransition.StateTransition(preState, block, validateStateRoot: true);

            // Add new state for this block to the store
            store.AddBlockState(signingRoot, state);

            _logger.LogInformation(Event.CreateGenesisStore, "Store added block {BeaconBlock} generating state {BeaconState}, with signing root {SigningRoot}",
                block, state, signingRoot);

            // Update justified checkpoint
            if (state.CurrentJustifiedCheckpoint.Epoch > store.JustifiedCheckpoint.Epoch)
            {
                store.SetBestJustifiedCheckpoint(state.CurrentJustifiedCheckpoint);
                var shouldUpdateJustifiedCheckpoint = ShouldUpdateJustifiedCheckpoint(store, state.CurrentJustifiedCheckpoint);
                if (shouldUpdateJustifiedCheckpoint)
                {
                    store.SetJustifiedCheckpoint(state.CurrentJustifiedCheckpoint);
                    _logger.LogDebug("Updated justified checkpoint {JustifiedCheckpoint}", state.CurrentJustifiedCheckpoint);
                }
                else
                {
                    _logger.LogDebug("Updated best justified checkpoint {JustifiedCheckpoint}", state.CurrentJustifiedCheckpoint);
                }
            }

            // Update finalized checkpoint
            if (state.FinalizedCheckpoint.Epoch > store.FinalizedCheckpoint.Epoch)
            {
                store.SetFinalizedCheckpoint(state.FinalizedCheckpoint);
                _logger.LogDebug("Updated finalized checkpoint {FinalizedCheckpoint}", state.FinalizedCheckpoint);
            }
        }

        /// <summary>
        /// To address the bouncing attack, only update conflicting justified
        /// checkpoints in the fork choice if in the early slots of the epoch.
        /// Otherwise, delay incorporation of new justified checkpoint until next epoch boundary.
        /// See https://ethresear.ch/t/prevention-of-bouncing-attack-on-ffg/6114 for more detailed analysis and discussion.
        /// </summary>
        public bool ShouldUpdateJustifiedCheckpoint(IStore store, Checkpoint newJustifiedCheckpoint)
        {
            var currentSlot = GetCurrentSlot(store);
            var slotsSinceEpochStart = ComputeSlotsSinceEpochStart(currentSlot);
            if (slotsSinceEpochStart < _forkChoiceConfigurationOptions.CurrentValue.SafeSlotsToUpdateJustified)
            {
                return true;
            }

            if (!store.TryGetBlock(newJustifiedCheckpoint.Root, out var newJustifiedBlock))
            {

            }
            var justifiedCheckpointEpochStartSlot = _beaconChainUtility.ComputeStartSlotOfEpoch(store.JustifiedCheckpoint.Epoch);
            if (newJustifiedBlock.Slot <= justifiedCheckpointEpochStartSlot)
            {
                return false;
            }
            if (!store.TryGetBlock(store.JustifiedCheckpoint.Root, out var justifiedCheckPointBlock))
            {

            }
            var ancestorOfNewCheckpointAtOldCheckpointSlot = GetAncestor(store, newJustifiedCheckpoint.Root, justifiedCheckPointBlock.Slot);
            if (ancestorOfNewCheckpointAtOldCheckpointSlot != store.JustifiedCheckpoint.Root)
            {
                return false;
            }
            // i.e. new checkpoint is descendent of old checkpoint
            return true;
        }

        public void OnTick(IStore store, ulong time)
        {
            var previousSlot = GetCurrentSlot(store);

            // update store time
            store.SetTime(time);

            var currentSlot = GetCurrentSlot(store);
            // Not a new epoch, return
            var isNewEpoch = (currentSlot > previousSlot) && (ComputeSlotsSinceEpochStart(currentSlot) == Slot.Zero);
            if (!isNewEpoch)
            {
                return;
            }
            // Update store.justified_checkpoint if a better checkpoint is known
            if (store.BestJustifiedCheckpoint.Epoch > store.JustifiedCheckpoint.Epoch)
            {
                store.SetJustifiedCheckpoint(store.BestJustifiedCheckpoint);
            }
        }
    }
}
