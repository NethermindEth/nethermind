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
using System.Threading.Tasks.Dataflow;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nethermind.BeaconNode.Configuration;
using Nethermind.BeaconNode.Containers;
using Nethermind.BeaconNode.Ssz;
using Nethermind.BeaconNode.Storage;
using Nethermind.Core2.Types;
using Nethermind.Logging.Microsoft;

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
            Epoch epoch = _beaconChainUtility.ComputeEpochAtSlot(slot);
            Slot startSlot = _beaconChainUtility.ComputeStartSlotOfEpoch(epoch);
            return slot - startSlot;
        }

        public async Task<Hash32> GetAncestorAsync(IStore store, Hash32 root, Slot slot)
        {
            // NOTE: This method should probably live in IStore, for various efficient implementations.

            BeaconBlock block = await store.GetBlockAsync(root).ConfigureAwait(false);

            if (block.Slot > slot)
            {
                return await GetAncestorAsync(store, block.ParentRoot, slot).ConfigureAwait(false);
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
            ulong slotValue = (store.Time - store.GenesisTime) / _timeParameterOptions.CurrentValue.SecondsPerSlot;
            return new Slot(slotValue);
        }

        public IStore GetGenesisStore(BeaconState genesisState)
        {
            MiscellaneousParameters miscellaneousParameters = _miscellaneousParameterOptions.CurrentValue;
            MaxOperationsPerBlock maxOperationsPerBlock = _maxOperationsPerBlockOptions.CurrentValue;

            Hash32 stateRoot = genesisState.HashTreeRoot(miscellaneousParameters, _timeParameterOptions.CurrentValue, _stateListLengthOptions.CurrentValue, maxOperationsPerBlock);
            BeaconBlock genesisBlock = new BeaconBlock(stateRoot);
            Hash32 root = genesisBlock.SigningRoot(miscellaneousParameters, maxOperationsPerBlock);
            Checkpoint justifiedCheckpoint = new Checkpoint(_initialValueOptions.CurrentValue.GenesisEpoch, root);
            Checkpoint finalizedCheckpoint = new Checkpoint(_initialValueOptions.CurrentValue.GenesisEpoch, root);

            if (_logger.IsInfo())
                Log.CreateGenesisStore(_logger, genesisBlock, genesisState, justifiedCheckpoint, root, null);

            Dictionary<Hash32, BeaconBlock> blocks = new Dictionary<Hash32, BeaconBlock>
            {
                [root] = genesisBlock
            };
            Dictionary<Hash32, BeaconState> blockStates = new Dictionary<Hash32, BeaconState>
            {
                [root] = BeaconState.Clone(genesisState)
            };
            Dictionary<Checkpoint, BeaconState> checkpointStates = new Dictionary<Checkpoint, BeaconState>
            {
                [justifiedCheckpoint] = BeaconState.Clone(genesisState)
            };

            IStore store = _storeProvider.CreateStore(
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
            // NOTE: This method should probably live in a separate object, for different implementations, possibly part of Store (for efficiency).

            // Execute the LMD-GHOST fork choice
            Hash32 head = store.JustifiedCheckpoint.Root;
            Slot justifiedSlot = _beaconChainUtility.ComputeStartSlotOfEpoch(store.JustifiedCheckpoint.Epoch);
            while (true)
            {
                List<Tuple<Hash32, Gwei>> childKeysWithBalances = new List<Tuple<Hash32, Gwei>>();
                await foreach (Hash32 childKey in store.GetChildKeysAfterSlotAsync(head, justifiedSlot)
                    .ConfigureAwait(false))
                {
                    Gwei balance = await GetLatestAttestingBalanceAsync(store, childKey).ConfigureAwait(false);
                    childKeysWithBalances.Add(Tuple.Create(childKey, balance));
                }
                if (childKeysWithBalances.Count == 0)
                {
                    return head;
                }

                head = childKeysWithBalances
                    .OrderByDescending(x => x.Item2)
                    .ThenByDescending(x => x.Item1)
                    .Select(x => x.Item1)
                    .First();
            }
        }

        public async Task<Gwei> GetLatestAttestingBalanceAsync(IStore store, Hash32 root)
        {
            // NOTE: This method should probably live in IStore, for various efficient implementations.

            Checkpoint justifiedCheckpoint = store.JustifiedCheckpoint;
            BeaconState state = (await store.GetCheckpointStateAsync(justifiedCheckpoint, true).ConfigureAwait(false))!;
            Epoch currentEpoch = _beaconStateAccessor.GetCurrentEpoch(state);
            IList<ValidatorIndex> activeIndexes = _beaconStateAccessor.GetActiveValidatorIndices(state, currentEpoch);
            BeaconBlock rootBlock = await store.GetBlockAsync(root).ConfigureAwait(false);
            
            Slot rootSlot = rootBlock!.Slot;
            Gwei balance = Gwei.Zero;
            foreach (ValidatorIndex index in activeIndexes)
            {
                LatestMessage? latestMessage = await store.GetLatestMessageAsync(index, false);
                if (latestMessage != null)
                {
                    Hash32 ancestor = await GetAncestorAsync(store, latestMessage.Root, rootSlot);
                    if (ancestor == root)
                    {
                        Validator validator = state.Validators[(int)index];
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
        public async Task OnAttestationAsync(IStore store, Attestation attestation)
        {
            if (_logger.IsInfo()) Log.OnAttestation(_logger, attestation, null);
            
            InitialValues initialValues = _initialValueOptions.CurrentValue;
            TimeParameters timeParameters = _timeParameterOptions.CurrentValue;

            Checkpoint target = attestation.Data.Target;

            // Attestations must be from the current or previous epoch
            Slot currentSlot = GetCurrentSlot(store);
            Epoch currentEpoch = _beaconChainUtility.ComputeEpochAtSlot(currentSlot);

            // Use GENESIS_EPOCH for previous when genesis to avoid underflow
            Epoch previousEpoch = currentEpoch > initialValues.GenesisEpoch
                ? currentEpoch - new Epoch(1)
                : initialValues.GenesisEpoch;

            if (target.Epoch != currentEpoch && target.Epoch != previousEpoch)
            {
                throw new ArgumentOutOfRangeException("attestation.Target.Epoch", target.Epoch, $"Attestation target epoch must be either the current epoch {currentEpoch} or previous epoch {previousEpoch}.");
            }
            // Cannot calculate the current shuffling if have not seen the target
            BeaconBlock targetBlock = await store.GetBlockAsync(target.Root).ConfigureAwait(false);

            // Attestations target be for a known block. If target block is unknown, delay consideration until the block is found
            BeaconState targetStoredState = await store.GetBlockStateAsync(target.Root).ConfigureAwait(false);

            // Attestations cannot be from future epochs. If they are, delay consideration until the epoch arrives
            BeaconState baseState = BeaconState.Clone(targetStoredState);
            Slot targetEpochStartSlot = _beaconChainUtility.ComputeStartSlotOfEpoch(target.Epoch);
            ulong targetEpochStartSlotTime = baseState.GenesisTime + (ulong)targetEpochStartSlot * timeParameters.SecondsPerSlot;
            if (store.Time < targetEpochStartSlotTime)
            {
                throw new Exception($"Ättestation target state time {targetEpochStartSlotTime} should not be larger than the store time {store.Time}).");
            }

            // Attestations must be for a known block. If block is unknown, delay consideration until the block is found
            BeaconBlock attestationBlock = await store.GetBlockAsync(attestation.Data.BeaconBlockRoot).ConfigureAwait(false);

            // Attestations must not be for blocks in the future. If not, the attestation should not be considered
            if (attestationBlock.Slot > attestation.Data.Slot)
            {
                throw new Exception($"Attestation data root slot {attestationBlock.Slot} should not be larger than the attestation data slot {attestation.Data.Slot}).");
            }

            // Store target checkpoint state if not yet seen

            BeaconState? targetState = await store.GetCheckpointStateAsync(target, false).ConfigureAwait(false);
            if (targetState == null)
            {
                _beaconStateTransition.ProcessSlots(baseState, targetEpochStartSlot);
                await store.SetCheckpointStateAsync(target, baseState).ConfigureAwait(false);
                targetState = baseState;
            }

            // Attestations can only affect the fork choice of subsequent slots.
            // Delay consideration in the fork choice until their slot is in the past.
            ulong attestationDataSlotTime = targetState!.GenesisTime + ((ulong)attestation.Data.Slot + 1) * timeParameters.SecondsPerSlot;
            if (store.Time < attestationDataSlotTime)
            {
                throw new Exception($"Attestation data time {attestationDataSlotTime} should not be larger than the store time {store.Time}).");
            }

            // Get state at the `target` to validate attestation and calculate the committees
            IndexedAttestation indexedAttestation = _beaconStateAccessor.GetIndexedAttestation(targetState, attestation);
            Domain domain = _beaconStateAccessor.GetDomain(targetState, _signatureDomainOptions.CurrentValue.BeaconAttester, indexedAttestation.Data.Target.Epoch);
            bool isValid = _beaconChainUtility.IsValidIndexedAttestation(targetState, indexedAttestation, domain);
            if (!isValid)
            {
                throw new Exception($"Indexed attestation {indexedAttestation} is not valid.");
            }

            // Update latest messages
            IEnumerable<ValidatorIndex> attestingIndices = _beaconStateAccessor.GetAttestingIndices(targetState, attestation.Data, attestation.AggregationBits);
            foreach (ValidatorIndex index in attestingIndices)
            {
                LatestMessage? latestMessage = await store.GetLatestMessageAsync(index, false).ConfigureAwait(false);
                if (latestMessage == null || target.Epoch > latestMessage!.Epoch)
                {
                    latestMessage = new LatestMessage(target.Epoch, attestation.Data.BeaconBlockRoot);
                    await store.SetLatestMessageAsync(index, latestMessage).ConfigureAwait(false);
                }
            }
        }

        public async Task OnBlockAsync(IStore store, BeaconBlock block)
        {
            Hash32 signingRoot = block.SigningRoot(_miscellaneousParameterOptions.CurrentValue, _maxOperationsPerBlockOptions.CurrentValue);
            
            if (_logger.IsInfo()) Log.OnBlock(_logger, signingRoot, block, null);
            
            // Make a copy of the state to avoid mutability issues
            BeaconState parentState = await store.GetBlockStateAsync(block.ParentRoot).ConfigureAwait(false);
            BeaconState preState = BeaconState.Clone(parentState);

            // Blocks cannot be in the future. If they are, their consideration must be delayed until the are in the past.
            ulong blockTime = preState.GenesisTime + (ulong)block.Slot * _timeParameterOptions.CurrentValue.SecondsPerSlot;
            if (blockTime > store.Time)
            {
                throw new ArgumentOutOfRangeException(nameof(block), blockTime, $"Block slot time cannot be in the future, compared to store time {store.Time}.");
            }

            // Add new block to the store
            await store.SetBlockAsync(signingRoot, block).ConfigureAwait(false);

            // Check block is a descendant of the finalized block
            BeaconBlock finalizedCheckpointBlock = await store.GetBlockAsync(store.FinalizedCheckpoint.Root).ConfigureAwait(false);
            Hash32 ancestor = await GetAncestorAsync(store, signingRoot, finalizedCheckpointBlock!.Slot).ConfigureAwait(false);
            if (ancestor != store.FinalizedCheckpoint.Root)
            {
                throw new Exception($"Block with signing root {signingRoot} is not a descendant of the finalized block {store.FinalizedCheckpoint.Root} at slot {finalizedCheckpointBlock.Slot}.");
            }

            // Check that block is later than the finalized epoch slot
            Slot finalizedEpochStartSlot = _beaconChainUtility.ComputeStartSlotOfEpoch(store.FinalizedCheckpoint.Epoch);
            if (block.Slot <= finalizedEpochStartSlot)
            {
                throw new ArgumentOutOfRangeException(nameof(block), block.Slot, $"Block slot must be later than the finalized epoch start slot {finalizedEpochStartSlot}.");
            }

            // Check the block is valid and compute the post-state
            BeaconState state = _beaconStateTransition.StateTransition(preState, block, validateStateRoot: true);

            // Add new state for this block to the store
            await store.SetBlockStateAsync(signingRoot, state).ConfigureAwait(false);

            if (_logger.IsDebug()) LogDebug.AddedBlockToStore(_logger, block, state, signingRoot, null);

            // Update justified checkpoint
            if (state.CurrentJustifiedCheckpoint.Epoch > store.JustifiedCheckpoint.Epoch)
            {
                await store.SetBestJustifiedCheckpointAsync(state.CurrentJustifiedCheckpoint).ConfigureAwait(false);
                bool shouldUpdateJustifiedCheckpoint = await ShouldUpdateJustifiedCheckpointAsync(store, state.CurrentJustifiedCheckpoint).ConfigureAwait(false);
                if (shouldUpdateJustifiedCheckpoint)
                {
                    await store.SetJustifiedCheckpointAsync(state.CurrentJustifiedCheckpoint).ConfigureAwait(false);
                    if (_logger.IsDebug()) LogDebug.UpdateJustifiedCheckpoint(_logger, state.CurrentJustifiedCheckpoint, null);
                }
                else
                {
                    if (_logger.IsDebug()) LogDebug.UpdateBestJustifiedCheckpoint(_logger, state.CurrentJustifiedCheckpoint, null);
                }
            }

            // Update finalized checkpoint
            if (state.FinalizedCheckpoint.Epoch > store.FinalizedCheckpoint.Epoch)
            {
                await store.SetFinalizedCheckpointAsync(state.FinalizedCheckpoint).ConfigureAwait(false);
                if (_logger.IsDebug()) LogDebug.UpdateFinalizedCheckpoint(_logger, state.FinalizedCheckpoint, null);
            }
        }

        public async Task OnTickAsync(IStore store, ulong time)
        {
            Slot previousSlot = GetCurrentSlot(store);

            // update store time
            await store.SetTimeAsync(time).ConfigureAwait(false);

            Slot currentSlot = GetCurrentSlot(store);
            // Not a new epoch, return
            bool isNewEpoch = (currentSlot > previousSlot) && (ComputeSlotsSinceEpochStart(currentSlot) == Slot.Zero);
            if (!isNewEpoch)
            {
                return;
            }
            Epoch currentEpoch = _beaconChainUtility.ComputeEpochAtSlot(currentSlot);

            if (_logger.IsInfo()) Log.OnTickNewEpoch(_logger, currentEpoch, currentSlot, store.Time, null);

            // Update store.justified_checkpoint if a better checkpoint is known
            if (store.BestJustifiedCheckpoint.Epoch > store.JustifiedCheckpoint.Epoch)
            {
                await store.SetJustifiedCheckpointAsync(store.BestJustifiedCheckpoint).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// To address the bouncing attack, only update conflicting justified
        /// checkpoints in the fork choice if in the early slots of the epoch.
        /// Otherwise, delay incorporation of new justified checkpoint until next epoch boundary.
        /// See https://ethresear.ch/t/prevention-of-bouncing-attack-on-ffg/6114 for more detailed analysis and discussion.
        /// </summary>
        public async Task<bool> ShouldUpdateJustifiedCheckpointAsync(IStore store, Checkpoint newJustifiedCheckpoint)
        {
            Slot currentSlot = GetCurrentSlot(store);
            Slot slotsSinceEpochStart = ComputeSlotsSinceEpochStart(currentSlot);
            if (slotsSinceEpochStart < _forkChoiceConfigurationOptions.CurrentValue.SafeSlotsToUpdateJustified)
            {
                return true;
            }

            BeaconBlock newJustifiedBlock = await store.GetBlockAsync(newJustifiedCheckpoint.Root).ConfigureAwait(false);
            Slot justifiedCheckpointEpochStartSlot = _beaconChainUtility.ComputeStartSlotOfEpoch(store.JustifiedCheckpoint.Epoch);
            if (newJustifiedBlock.Slot <= justifiedCheckpointEpochStartSlot)
            {
                return false;
            }

            BeaconBlock justifiedCheckPointBlock = await store.GetBlockAsync(store.JustifiedCheckpoint.Root).ConfigureAwait(false);
            
            Hash32 ancestorOfNewCheckpointAtOldCheckpointSlot = await GetAncestorAsync(store, newJustifiedCheckpoint.Root, justifiedCheckPointBlock.Slot).ConfigureAwait(false);
            
            // i.e. new checkpoint is descendant of old checkpoint
            return ancestorOfNewCheckpointAtOldCheckpointSlot == store.JustifiedCheckpoint.Root;
        }
    }
}
