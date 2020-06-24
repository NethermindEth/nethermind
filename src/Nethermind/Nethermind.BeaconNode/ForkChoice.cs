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
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nethermind.Core2;
using Nethermind.Core2.Configuration;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Store;
using Nethermind.Core2.Types;
using Nethermind.Logging.Microsoft;

namespace Nethermind.BeaconNode
{
    public class ForkChoice : IForkChoice
    {
        private readonly IBeaconChainUtility _beaconChainUtility;
        private readonly BeaconStateAccessor _beaconStateAccessor;
        private readonly BeaconStateTransition _beaconStateTransition;
        private readonly ChainConstants _chainConstants;
        private readonly ICryptographyService _cryptographyService;
        private readonly IOptionsMonitor<ForkChoiceConfiguration> _forkChoiceConfigurationOptions;
        private readonly ILogger _logger;
        private readonly IOptionsMonitor<MaxOperationsPerBlock> _maxOperationsPerBlockOptions;
        private readonly IOptionsMonitor<MiscellaneousParameters> _miscellaneousParameterOptions;
        private readonly IOptionsMonitor<SignatureDomains> _signatureDomainOptions;
        private readonly IOptionsMonitor<TimeParameters> _timeParameterOptions;

        public ForkChoice(
            ILogger<ForkChoice> logger,
            ChainConstants chainConstants,
            IOptionsMonitor<MiscellaneousParameters> miscellaneousParameterOptions,
            IOptionsMonitor<TimeParameters> timeParameterOptions,
            IOptionsMonitor<MaxOperationsPerBlock> maxOperationsPerBlockOptions,
            IOptionsMonitor<ForkChoiceConfiguration> forkChoiceConfigurationOptions,
            IOptionsMonitor<SignatureDomains> signatureDomainOptions,
            ICryptographyService cryptographyService,
            IBeaconChainUtility beaconChainUtility,
            BeaconStateAccessor beaconStateAccessor,
            BeaconStateTransition beaconStateTransition)
        {
            _logger = logger;
            _chainConstants = chainConstants;
            _miscellaneousParameterOptions = miscellaneousParameterOptions;
            _timeParameterOptions = timeParameterOptions;
            _maxOperationsPerBlockOptions = maxOperationsPerBlockOptions;
            _forkChoiceConfigurationOptions = forkChoiceConfigurationOptions;
            _signatureDomainOptions = signatureDomainOptions;
            _cryptographyService = cryptographyService;
            _beaconChainUtility = beaconChainUtility;
            _beaconStateAccessor = beaconStateAccessor;
            _beaconStateTransition = beaconStateTransition;
        }

        public Slot ComputeSlotsSinceEpochStart(Slot slot)
        {
            Epoch epoch = _beaconChainUtility.ComputeEpochAtSlot(slot);
            Slot startSlot = _beaconChainUtility.ComputeStartSlotOfEpoch(epoch);
            return slot - startSlot;
        }

        public async Task<Root> GetAncestorAsync(IStore store, Root root, Slot slot)
        {
            return await store.GetAncestorAsync(root, slot).ConfigureAwait(false);
        }

        public Slot GetCurrentSlot(IStore store)
        {
            Slot slotsSinceGenesis = GetSlotsSinceGenesis(store);
            Slot slot = _chainConstants.GenesisSlot + slotsSinceGenesis;
            return slot;
        }

        public async Task<Root> GetHeadAsync(IStore store)
        {
            return await store.GetHeadAsync().ConfigureAwait(false);
        }

        public Slot GetSlotsSinceGenesis(IStore store)
        {
            ulong secondsSinceGenesis = store.Time - store.GenesisTime;
            Slot slotsSinceGenesis = new Slot(secondsSinceGenesis / _timeParameterOptions.CurrentValue.SecondsPerSlot);
            return slotsSinceGenesis;
        }

        public async Task InitializeForkChoiceStoreAsync(IStore store, BeaconState anchorState)
        {
            // Implements the logic for get_genesis_store / get_forkchoice_store

            Root stateRoot = !anchorState.LatestBlockHeader.StateRoot.Equals(Root.Zero)
                ? anchorState.LatestBlockHeader.StateRoot
                : _cryptographyService.HashTreeRoot(anchorState);

            BeaconBlock anchorBlock = new BeaconBlock(anchorState.Slot, anchorState.LatestBlockHeader.ParentRoot,
                stateRoot, BeaconBlockBody.Zero);
            SignedBeaconBlock signedAnchorBlock = new SignedBeaconBlock(anchorBlock, BlsSignature.Zero);

            Root anchorRoot = _cryptographyService.HashTreeRoot(anchorBlock);
            Epoch anchorEpoch = _beaconStateAccessor.GetCurrentEpoch(anchorState);
            Checkpoint justifiedCheckpoint = new Checkpoint(anchorEpoch, anchorRoot);
            Checkpoint finalizedCheckpoint = new Checkpoint(anchorEpoch, anchorRoot);

            if (_logger.IsInfo())
                Log.CreateGenesisStore(_logger, anchorState.Fork, anchorRoot, anchorState.GenesisTime, anchorState,
                    anchorBlock, justifiedCheckpoint, null);

            Dictionary<Root, SignedBeaconBlock> signedBlocks = new Dictionary<Root, SignedBeaconBlock>
            {
                [anchorRoot] = signedAnchorBlock
            };
            Dictionary<Root, BeaconState> blockStates = new Dictionary<Root, BeaconState>
            {
                [anchorRoot] = BeaconState.Clone(anchorState)
            };
            Dictionary<Checkpoint, BeaconState> checkpointStates = new Dictionary<Checkpoint, BeaconState>
            {
                [justifiedCheckpoint] = BeaconState.Clone(anchorState)
            };

            await store.InitializeForkChoiceStoreAsync(
                anchorState.GenesisTime,
                anchorState.GenesisTime,
                justifiedCheckpoint,
                finalizedCheckpoint,
                justifiedCheckpoint,
                signedBlocks,
                blockStates,
                checkpointStates);
        }

        /// <summary>
        /// Run ``on_attestation`` upon receiving a new ``attestation`` from either within a block or directly on the wire.
        /// An ``attestation`` that is asserted as invalid may be valid at a later time,
        /// consider scheduling it for later processing in such case.
        /// </summary>
        public async Task OnAttestationAsync(IStore store, Attestation attestation)
        {
            if (_logger.IsInfo()) Log.OnAttestation(_logger, attestation, null);

            TimeParameters timeParameters = _timeParameterOptions.CurrentValue;

            Checkpoint target = attestation.Data.Target;

            // Attestations must be from the current or previous epoch
            Slot currentSlot = GetCurrentSlot(store);
            Epoch currentEpoch = _beaconChainUtility.ComputeEpochAtSlot(currentSlot);

            // Use GENESIS_EPOCH for previous when genesis to avoid underflow
            Epoch previousEpoch = currentEpoch > _chainConstants.GenesisEpoch
                ? currentEpoch - new Epoch(1)
                : _chainConstants.GenesisEpoch;
            if (target.Epoch != currentEpoch && target.Epoch != previousEpoch)
            {
                throw new ArgumentOutOfRangeException("attestation.Target.Epoch", target.Epoch,
                    $"Attestation target epoch must be either the current epoch {currentEpoch} or previous epoch {previousEpoch}.");
            }

            Epoch dataSlotEpoch = _beaconChainUtility.ComputeEpochAtSlot(attestation.Data.Slot);
            if (attestation.Data.Target.Epoch != dataSlotEpoch)
            {
                throw new ArgumentOutOfRangeException("attestation.Data.Target.Epoch", attestation.Data.Target.Epoch,
                    $"Attestation data target epoch must match the attestation data slot {attestation.Data.Slot} (epoch {dataSlotEpoch}).");
            }

            // Attestations target be for a known block. If target block is unknown, delay consideration until the block is found
            BeaconBlock targetBlock = (await store.GetSignedBlockAsync(target.Root).ConfigureAwait(false)).Message;

            // Attestations cannot be from future epochs. If they are, delay consideration until the epoch arrives
            BeaconState targetStoredState = await store.GetBlockStateAsync(target.Root).ConfigureAwait(false);
            BeaconState baseState = BeaconState.Clone(targetStoredState);
            Slot targetEpochStartSlot = _beaconChainUtility.ComputeStartSlotOfEpoch(target.Epoch);
            if (currentSlot < targetEpochStartSlot)
            {
                throw new Exception(
                    $"Ättestation target epoch start slot {targetEpochStartSlot} should not be larger than the store current slot {currentSlot}).");
            }

            // Attestations must be for a known block. If block is unknown, delay consideration until the block is found
            BeaconBlock attestationBlock =
                (await store.GetSignedBlockAsync(attestation.Data.BeaconBlockRoot).ConfigureAwait(false)).Message;

            // Attestations must not be for blocks in the future. If not, the attestation should not be considered
            if (attestationBlock.Slot > attestation.Data.Slot)
            {
                throw new Exception(
                    $"Attestation data root slot {attestationBlock.Slot} should not be larger than the attestation data slot {attestation.Data.Slot}).");
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
            Slot newCurrentSlot = GetCurrentSlot(store);
            if (newCurrentSlot < attestation.Data.Slot + 1)
            {
                throw new Exception(
                    $"Attestation data slot {attestation.Data.Slot} should not be larger than the store current slot {newCurrentSlot}).");
            }

            // Get state at the `target` to validate attestation and calculate the committees
            IndexedAttestation indexedAttestation =
                _beaconStateAccessor.GetIndexedAttestation(targetState, attestation);
            Domain domain = _beaconStateAccessor.GetDomain(targetState,
                _signatureDomainOptions.CurrentValue.BeaconAttester, indexedAttestation.Data.Target.Epoch);
            bool isValid = _beaconChainUtility.IsValidIndexedAttestation(targetState, indexedAttestation, domain);
            if (!isValid)
            {
                throw new Exception($"Indexed attestation {indexedAttestation} is not valid.");
            }

            // Update latest messages
            IEnumerable<ValidatorIndex> attestingIndices =
                _beaconStateAccessor.GetAttestingIndices(targetState, attestation.Data, attestation.AggregationBits);
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

        public async Task OnBlockAsync(IStore store, SignedBeaconBlock signedBlock)
        {
            MiscellaneousParameters miscellaneousParameters = _miscellaneousParameterOptions.CurrentValue;
            MaxOperationsPerBlock maxOperationsPerBlock = _maxOperationsPerBlockOptions.CurrentValue;

            BeaconBlock block = signedBlock.Message;
            Root blockRoot = _cryptographyService.HashTreeRoot(block);

            if (_logger.IsInfo()) Log.OnBlock(_logger, blockRoot, block, signedBlock.Signature, null);

            // Make a copy of the state to avoid mutability issues
            BeaconState parentState = await store.GetBlockStateAsync(block.ParentRoot).ConfigureAwait(false);
            BeaconState preState = BeaconState.Clone(parentState);

            // Blocks cannot be in the future. If they are, their consideration must be delayed until the are in the past.
            ulong storeTime = store.Time;
            Slot storeCurrentSlot = GetCurrentSlot(store);
            if (storeCurrentSlot < block.Slot)
            {
                throw new ArgumentOutOfRangeException(nameof(block), block.Slot,
                    $"Block slot time cannot be in the future, compared to store time {storeTime} (slot {storeCurrentSlot}, since genesis {store.GenesisTime}.");
            }

            // Add new block to the store
            await store.SetSignedBlockAsync(blockRoot, signedBlock).ConfigureAwait(false);

            // Check that block is later than the finalized epoch slot (optimization to reduce calls to get_ancestor)
            Slot finalizedSlot = _beaconChainUtility.ComputeStartSlotOfEpoch(store.FinalizedCheckpoint.Epoch);
            if (block.Slot <= finalizedSlot)
            {
                throw new ArgumentOutOfRangeException(nameof(block), block.Slot,
                    $"Block slot must be later than the finalized epoch start slot {finalizedSlot}.");
            }

            // Check block is a descendant of the finalized block at the checkpoint finalized slot
            Root ancestorOfBlockAtFinalizedSlot =
                await GetAncestorAsync(store, blockRoot, finalizedSlot).ConfigureAwait(false);
            if (!ancestorOfBlockAtFinalizedSlot.Equals(store.FinalizedCheckpoint.Root))
            {
                throw new Exception(
                    $"Block with hash tree root {blockRoot} is not a descendant of the finalized block {store.FinalizedCheckpoint.Root} at slot {finalizedSlot}.");
            }

            // Check the block is valid and compute the post-state
            BeaconState state = _beaconStateTransition.StateTransition(preState, signedBlock, validateResult: true);

            // Add new state for this block to the store
            await store.SetBlockStateAsync(blockRoot, state).ConfigureAwait(false);

            if (_logger.IsDebug()) LogDebug.AddedBlockToStore(_logger, block, state, blockRoot, null);

            // Update justified checkpoint
            if (state.CurrentJustifiedCheckpoint.Epoch > store.JustifiedCheckpoint.Epoch)
            {
                if (state.CurrentJustifiedCheckpoint.Epoch > store.BestJustifiedCheckpoint.Epoch)
                {
                    await store.SetBestJustifiedCheckpointAsync(state.CurrentJustifiedCheckpoint).ConfigureAwait(false);
                }

                bool shouldUpdateJustifiedCheckpoint =
                    await ShouldUpdateJustifiedCheckpointAsync(store, state.CurrentJustifiedCheckpoint)
                        .ConfigureAwait(false);
                if (shouldUpdateJustifiedCheckpoint)
                {
                    await store.SetJustifiedCheckpointAsync(state.CurrentJustifiedCheckpoint).ConfigureAwait(false);
                    if (_logger.IsDebug())
                        LogDebug.UpdateJustifiedCheckpoint(_logger, state.CurrentJustifiedCheckpoint, null);
                }
                else
                {
                    if (_logger.IsDebug())
                        LogDebug.UpdateBestJustifiedCheckpoint(_logger, state.CurrentJustifiedCheckpoint, null);
                }
            }

            // Update finalized checkpoint
            if (state.FinalizedCheckpoint.Epoch > store.FinalizedCheckpoint.Epoch)
            {
                await store.SetFinalizedCheckpointAsync(state.FinalizedCheckpoint).ConfigureAwait(false);
                if (_logger.IsDebug()) LogDebug.UpdateFinalizedCheckpoint(_logger, state.FinalizedCheckpoint, null);

                // Update justified if new justified is later than store justified
                // or if store justified is not in chain with finalized checkpoint
                if (state.CurrentJustifiedCheckpoint.Epoch > store.JustifiedCheckpoint.Epoch)
                {
                    await store.SetJustifiedCheckpointAsync(state.CurrentJustifiedCheckpoint).ConfigureAwait(false);
                    if (_logger.IsDebug())
                        LogDebug.UpdateJustifiedCheckpoint(_logger, state.CurrentJustifiedCheckpoint, null);
                }
                else
                {
                    Slot newFinalizedSlot =
                        _beaconChainUtility.ComputeStartSlotOfEpoch(store.FinalizedCheckpoint.Epoch);
                    Root ancestorOfJustifiedAtNewFinalizedSlot =
                        await GetAncestorAsync(store, store.JustifiedCheckpoint.Root, newFinalizedSlot)
                            .ConfigureAwait(false);
                    if (!ancestorOfJustifiedAtNewFinalizedSlot.Equals(store.FinalizedCheckpoint.Root))
                    {
                        await store.SetJustifiedCheckpointAsync(state.CurrentJustifiedCheckpoint).ConfigureAwait(false);
                        if (_logger.IsDebug())
                            LogDebug.UpdateJustifiedCheckpoint(_logger, state.CurrentJustifiedCheckpoint, null);
                    }
                }
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

            Slot justifiedSlot = _beaconChainUtility.ComputeStartSlotOfEpoch(store.JustifiedCheckpoint.Epoch);
            Root ancestorAtJustifiedSlot = await GetAncestorAsync(store, newJustifiedCheckpoint.Root, justifiedSlot)
                .ConfigureAwait(false);
            return ancestorAtJustifiedSlot.Equals(store.JustifiedCheckpoint.Root);
        }
    }
}