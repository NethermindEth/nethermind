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
using Microsoft.Extensions.Options;
using Nethermind.BeaconNode.Configuration;
using Nethermind.BeaconNode.Containers;
using Nethermind.BeaconNode.Ssz;
using Nethermind.BeaconNode.Storage;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode
{
    public class ForkChoice
    {
        private readonly BeaconChainUtility _beaconChainUtility;
        private readonly BeaconStateAccessor _beaconStateAccessor;
        private readonly BeaconStateTransition _beaconStateTransition;
        private readonly IOptionsMonitor<ForkChoiceConfiguration> _forkChoiceConfigurationOptions;
        private readonly IOptionsMonitor<InitialValues> _initialValueOptions;
        private readonly ILogger _logger;
        private readonly IOptionsMonitor<MaxOperationsPerBlock> _maxOperationsPerBlockOptions;
        private readonly IOptionsMonitor<MiscellaneousParameters> _miscellaneousParameterOptions;
        private readonly IOptionsMonitor<SignatureDomains> _signatureDomainOptions;
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
            IOptionsMonitor<SignatureDomains> signatureDomainOptions,
            BeaconChainUtility beaconChainUtility,
            BeaconStateAccessor beaconStateAccessor,
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
            _signatureDomainOptions = signatureDomainOptions;
            _beaconChainUtility = beaconChainUtility;
            _beaconStateAccessor = beaconStateAccessor;
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

            if (!store.TryGetBlock(root, out BeaconBlock? block) || block is null)
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
                checkpointStates,
                new Dictionary<ValidatorIndex, LatestMessage>()
                );
            return store;
        }

        public async Task<Hash32> GetHeadAsync(IStore store)
        {
            return await Task.Run(() =>
            {
                // Execute the LMD-GHOST fork choice
                var head = store.JustifiedCheckpoint.Root;
                var justifiedSlot = _beaconChainUtility.ComputeStartSlotOfEpoch(store.JustifiedCheckpoint.Epoch);
                while (true)
                {
                    var children = store.Blocks
                        .Where(kvp =>
                            kvp.Value.ParentRoot.Equals(head)
                            && kvp.Value.Slot > justifiedSlot)
                        .Select(kvp => kvp.Key);
                    if (children.Count() == 0)
                    {
                        return head;
                    }
                    head = children
                        .OrderByDescending(x => GetLatestAttestingBalance(store, x))
                        .ThenByDescending(x => x)
                        .First();
                }
            });
        }

        public Gwei GetLatestAttestingBalance(IStore store, Hash32 root)
        {
            if (!store.TryGetCheckpointState(store.JustifiedCheckpoint, out var storedState))
            {
                throw new Exception($"Not able to get checkpoint state {store.JustifiedCheckpoint}");
            }

            var state = storedState!;
            var currentEpoch = _beaconStateAccessor.GetCurrentEpoch(state);
            var activeIndexes = _beaconStateAccessor.GetActiveValidatorIndices(state, currentEpoch);
            if (!store.TryGetBlock(root, out var rootBlock))
            {
                throw new Exception($"Not ble to find block {root}");
            }
            
            Slot rootSlot = rootBlock!.Slot;
            Gwei balance = Gwei.Zero;
            foreach (ValidatorIndex index in activeIndexes)
            {
                if (store.TryGetLatestMessage(index, out var latestMessage))
                {
                    var ancestor = GetAncestor(store, latestMessage!.Root, rootSlot);
                    if (ancestor == root)
                    {
                        var validator = state.Validators[(int)index];
                        balance += validator.EffectiveBalance;
                    }
                }
            }
            
            return balance;
        }

        /// <summary>
        /// Run ``on_attestation`` upon receiving a new ``attestation`` from either within a block or directly on the wire.
        /// An ``attestation`` that is asserted as invalid may be valid at a later time,
        /// consider scheduling it for later processing in such case.
        /// </summary>
        public void OnAttestation(IStore store, Attestation attestation)
        {
            var initialValues = _initialValueOptions.CurrentValue;
            var timeParameters = _timeParameterOptions.CurrentValue;

            var target = attestation.Data.Target;

            // Attestations must be from the current or previous epoch
            var currentSlot = GetCurrentSlot(store);
            var currentEpoch = _beaconChainUtility.ComputeEpochAtSlot(currentSlot);

            // Use GENESIS_EPOCH for previous when genesis to avoid underflow
            var previousEpoch = currentEpoch > initialValues.GenesisEpoch
                ? currentEpoch - new Epoch(1)
                : initialValues.GenesisEpoch;

            if (target.Epoch != currentEpoch && target.Epoch != previousEpoch)
            {
                throw new ArgumentOutOfRangeException("attestation.Target.Epoch", target.Epoch, $"Attestation target epoch must be either the current epoch {currentEpoch} or previous epoch {previousEpoch}.");
            }
            // Cannot calculate the current shuffling if have not seen the target
            if (!store.TryGetBlock(target.Root, out var targetBlock))
            {
                throw new ArgumentOutOfRangeException("attestation.Target.Root", target.Root, "Attestation target root not found in the block history.");
            }

            // Attestations target be for a known block. If target block is unknown, delay consideration until the block is found
            if (!store.TryGetBlockState(target.Root, out var targetStoredState))
            {
                throw new ArgumentOutOfRangeException("attestation.Target.Root", target.Root, "Attestation target root not found in the block stores history.");
            }

            // Attestations cannot be from future epochs. If they are, delay consideration until the epoch arrives
            var baseState = BeaconState.Clone(targetStoredState!);
            var targetEpochStartSlot = _beaconChainUtility.ComputeStartSlotOfEpoch(target.Epoch);
            var targetEpochStartSlotTime = baseState.GenesisTime + (ulong)targetEpochStartSlot * timeParameters.SecondsPerSlot;
            if (store.Time < targetEpochStartSlotTime)
            {
                throw new Exception($"Ättestation target state time {targetEpochStartSlotTime} should not be larger than the store time {store.Time}).");
            }

            // Attestations must be for a known block. If block is unknown, delay consideration until the block is found
            if (!store.TryGetBlock(attestation.Data.BeaconBlockRoot, out var storedAttestationBlock))
            {
                throw new ArgumentOutOfRangeException("attestation.Data.BeaconBlockRoot", attestation.Data.BeaconBlockRoot, "Attestation data root not found in the block history.");
            }

            var attestationBlock = storedAttestationBlock!;
            // Attestations must not be for blocks in the future. If not, the attestation should not be considered
            if (attestationBlock.Slot > attestation.Data.Slot)
            {
                throw new Exception($"Attestation data root slot {attestationBlock.Slot} should not be larger than the attestation data slot {attestation.Data.Slot}).");
            }

            // Store target checkpoint state if not yet seen

            if (!store.TryGetCheckpointState(target, out var targetState))
            {
                _beaconStateTransition.ProcessSlots(baseState, targetEpochStartSlot);
                store.SetCheckpointState(target, baseState);
                targetState = baseState;
            }

            // Attestations can only affect the fork choice of subsequent slots.
            // Delay consideration in the fork choice until their slot is in the past.
            //var attestationDataSlotTime = ((ulong)attestation.Data.Slot + 1) * timeParameters.SecondsPerSlot;
            ulong attestationDataSlotTime = targetState!.GenesisTime + ((ulong)attestation.Data.Slot + 1) * timeParameters.SecondsPerSlot;
            if (store.Time < attestationDataSlotTime)
            {
                throw new Exception($"Ättestation data time {attestationDataSlotTime} should not be larger than the store time {store.Time}).");
            }

            // Get state at the `target` to validate attestation and calculate the committees
            var indexedAttestation = _beaconStateAccessor.GetIndexedAttestation(targetState, attestation);
            var domain = _beaconStateAccessor.GetDomain(targetState, _signatureDomainOptions.CurrentValue.BeaconAttester, indexedAttestation.Data.Target.Epoch);
            var isValid = _beaconChainUtility.IsValidIndexedAttestation(targetState, indexedAttestation, domain);
            if (!isValid)
            {
                throw new Exception($"Indexed attestation {indexedAttestation} is not valid.");
            }

            // Update latest messages
            var attestingIndices = _beaconStateAccessor.GetAttestingIndices(targetState, attestation.Data, attestation.AggregationBits);
            foreach (var index in attestingIndices)
            {
                if (!store.TryGetLatestMessage(index, out var latestMessage) || target.Epoch > latestMessage!.Epoch)
                {
                    latestMessage = new LatestMessage(target.Epoch, attestation.Data.BeaconBlockRoot);
                    store.SetLatestMessage(index, latestMessage);
                }
            }
        }

        public void OnBlock(IStore store, BeaconBlock block)
        {
            // Make a copy of the state to avoid mutability issues
            if (!store.TryGetBlockState(block.ParentRoot, out BeaconState? parentState) || parentState is null)
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
            store.SetBlock(signingRoot, block);

            // Check block is a descendant of the finalized block
            if (!store.TryGetBlock(store.FinalizedCheckpoint.Root, out var finalizedCheckpointBlock))
            {
                throw new Exception($"Block not found for finalized checkpoint root {store.FinalizedCheckpoint.Root}.");
            }
            
            Hash32 ancestor = GetAncestor(store, signingRoot, finalizedCheckpointBlock!.Slot);
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
            store.SetBlockState(signingRoot, state);

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
            var currentEpoch = _beaconChainUtility.ComputeEpochAtSlot(currentSlot);
            _logger.LogDebug("New epoch {Epoch} at time {Time:n0}", currentEpoch, store.Time);
            // Update store.justified_checkpoint if a better checkpoint is known
            if (store.BestJustifiedCheckpoint.Epoch > store.JustifiedCheckpoint.Epoch)
            {
                store.SetJustifiedCheckpoint(store.BestJustifiedCheckpoint);
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
                throw new NotImplementedException("What if block is null");
            }
            
            Slot justifiedCheckpointEpochStartSlot = _beaconChainUtility.ComputeStartSlotOfEpoch(store.JustifiedCheckpoint.Epoch);
            if (newJustifiedBlock!.Slot <= justifiedCheckpointEpochStartSlot)
            {
                return false;
            }
            
            if (!store.TryGetBlock(store.JustifiedCheckpoint.Root, out var justifiedCheckPointBlock))
            {
                throw new NotImplementedException("What if justified checkpoint block is null");
            }
            
            Hash32 ancestorOfNewCheckpointAtOldCheckpointSlot = GetAncestor(store, newJustifiedCheckpoint.Root, justifiedCheckPointBlock!.Slot);
            
            // i.e. new checkpoint is descendant of old checkpoint
            return ancestorOfNewCheckpointAtOldCheckpointSlot == store.JustifiedCheckpoint.Root;
        }
    }
}
