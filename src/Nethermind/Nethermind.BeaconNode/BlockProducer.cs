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
using Nethermind.Core2.Configuration;
using Nethermind.BeaconNode.Services;
using Nethermind.BeaconNode.Ssz;
using Nethermind.BeaconNode.Storage;
using Nethermind.Core2;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using Nethermind.Logging.Microsoft;
using Attestation = Nethermind.BeaconNode.Containers.Attestation;
using AttesterSlashing = Nethermind.BeaconNode.Containers.AttesterSlashing;
using BeaconBlock = Nethermind.BeaconNode.Containers.BeaconBlock;
using BeaconBlockBody = Nethermind.BeaconNode.Containers.BeaconBlockBody;
using BeaconState = Nethermind.BeaconNode.Containers.BeaconState;
using Deposit = Nethermind.BeaconNode.Containers.Deposit;
using Eth1Data = Nethermind.BeaconNode.Containers.Eth1Data;
using Hash32 = Nethermind.Core2.Types.Hash32;
using ProposerSlashing = Nethermind.BeaconNode.Containers.ProposerSlashing;

namespace Nethermind.BeaconNode
{
    public class BlockProducer
    {
        private readonly ILogger _logger;
        private readonly IOptionsMonitor<MiscellaneousParameters> _miscellaneousParameterOptions;
        private readonly IOptionsMonitor<TimeParameters> _timeParameterOptions;
        private readonly IOptionsMonitor<StateListLengths> _stateListLengthOptions;
        private readonly IOptionsMonitor<MaxOperationsPerBlock> _maxOperationsPerBlockOptions;
        private readonly IOptionsMonitor<HonestValidatorConstants> _honestValidatorConstantOptions;
        private readonly BeaconStateTransition _beaconStateTransition;
        private readonly ForkChoice _forkChoice;
        private readonly IStoreProvider _storeProvider;
        private readonly IEth1DataProvider _eth1DataProvider;
        private readonly IOperationPool _operationPool;

        public BlockProducer(ILogger<BlockProducer> logger, 
            IOptionsMonitor<MiscellaneousParameters> miscellaneousParameterOptions,
            IOptionsMonitor<TimeParameters> timeParameterOptions,
            IOptionsMonitor<StateListLengths> stateListLengthOptions,
            IOptionsMonitor<MaxOperationsPerBlock> maxOperationsPerBlockOptions,
            IOptionsMonitor<HonestValidatorConstants> honestValidatorConstantOptions,
            BeaconStateTransition beaconStateTransition,
            ForkChoice forkChoice, 
            IStoreProvider storeProvider,
            IEth1DataProvider eth1DataProvider,
            IOperationPool operationPool)
        {
            _logger = logger;
            _miscellaneousParameterOptions = miscellaneousParameterOptions;
            _timeParameterOptions = timeParameterOptions;
            _stateListLengthOptions = stateListLengthOptions;
            _maxOperationsPerBlockOptions = maxOperationsPerBlockOptions;
            _honestValidatorConstantOptions = honestValidatorConstantOptions;
            _beaconStateTransition = beaconStateTransition;
            _forkChoice = forkChoice;
            _storeProvider = storeProvider;
            _eth1DataProvider = eth1DataProvider;
            _operationPool = operationPool;
        }
        
        public async Task<BeaconBlock> NewBlockAsync(Slot slot, BlsSignature randaoReveal)
        {
            if (slot == Slot.Zero)
            {
                throw new ArgumentException("Can't generate new block for slot 0, as it is the genesis block.");
            }
            
            if (!_storeProvider.TryGetStore(out IStore? retrievedStore))
            {
                throw new Exception("Beacon chain is currently syncing or waiting for genesis.");
            }
            IStore store = retrievedStore!;

            Slot previousSlot = slot - Slot.One;
            Hash32 head = await _forkChoice.GetHeadAsync(store).ConfigureAwait(false);
            BeaconBlock headBlock = await store.GetBlockAsync(head).ConfigureAwait(false);

            BeaconBlock parentBlock;
            BeaconState parentState;
            Hash32 parentRoot;
            if (headBlock!.Slot > previousSlot)
            {
                // Requesting a block for a past slot?
                Hash32 ancestorSigningRoot = await _forkChoice.GetAncestorAsync(store, head, previousSlot);
                parentBlock = await store.GetBlockAsync(ancestorSigningRoot).ConfigureAwait(false);
                parentState = await store.GetBlockStateAsync(ancestorSigningRoot).ConfigureAwait(false);
                parentRoot = ancestorSigningRoot;
            }
            else
            {
                parentBlock = headBlock!;
                if (parentBlock.Slot < previousSlot)
                {
                    if (_logger.IsDebug()) LogDebug.NewBlockSkippedSlots(_logger, slot, randaoReveal, parentBlock.Slot, null);
                }
                parentState = await store.GetBlockStateAsync(head).ConfigureAwait(false);
                parentRoot = head;
            }

            // Clone state (will mutate) and process outstanding slots
            BeaconState state = BeaconState.Clone(parentState);
            _beaconStateTransition.ProcessSlots(state, slot);

            MaxOperationsPerBlock maxOperationsPerBlock = _maxOperationsPerBlockOptions.CurrentValue;

            // Eth 1 Data
            Eth1Data eth1Vote = await GetEth1VoteAsync(state, store, parentRoot).ConfigureAwait(false);

            List<Deposit> deposits = new List<Deposit>();
            if (eth1Vote.DepositCount > state.Eth1DepositIndex)
            {
                await foreach (Deposit deposit in _eth1DataProvider.GetDepositsAsync(eth1Vote.DepositRoot,
                    state.Eth1DepositIndex, maxOperationsPerBlock.MaximumDeposits).ConfigureAwait(false))
                {
                    deposits.Add(deposit);
                }
            }

            // Operations
            List<Attestation> attestations = new List<Attestation>();
            await foreach (Attestation attestation in _operationPool.GetAttestationsAsync(
                maxOperationsPerBlock.MaximumAttestations).ConfigureAwait(false))
            {
                attestations.Add(attestation);
            }

            List<AttesterSlashing> attesterSlashings = new List<AttesterSlashing>();
            await foreach (AttesterSlashing attesterSlashing in _operationPool.GetAttesterSlashingsAsync(
                maxOperationsPerBlock.MaximumAttesterSlashings).ConfigureAwait(false))
            {
                attesterSlashings.Add(attesterSlashing);
            }
            
            List<ProposerSlashing> proposerSlashings = new List<ProposerSlashing>();
            await foreach (ProposerSlashing proposerSlashing in _operationPool.GetProposerSlashingsAsync(
                maxOperationsPerBlock.MaximumProposerSlashings).ConfigureAwait(false))
            {
                proposerSlashings.Add(proposerSlashing);
            }

            List<VoluntaryExit> voluntaryExits = new List<VoluntaryExit>();
            await foreach (VoluntaryExit voluntaryExit in _operationPool.GetVoluntaryExits(
                maxOperationsPerBlock.MaximumVoluntaryExits).ConfigureAwait(false))
            {
                voluntaryExits.Add(voluntaryExit);
            }
            
            // Graffiti
            var graffitiBytes = new byte[32];
            graffitiBytes[0] = 0x4e; // 'N'
            Bytes32 graffiti = new Bytes32(graffitiBytes);

            // Build block
            BeaconBlockBody body = new BeaconBlockBody(randaoReveal, eth1Vote, graffiti, proposerSlashings,
                attesterSlashings, attestations, deposits, voluntaryExits);
            BeaconBlock block = new BeaconBlock(slot, parentRoot, Hash32.Zero, body, BlsSignature.Empty);
            
            // Apply block to state transition and calculate resulting state root
            Hash32 stateRoot = ComputeNewStateRoot(state, block);
            block.SetStateRoot(stateRoot);

            // Unsigned block

            if (_logger.IsDebug())
                LogDebug.NewBlockProduced(_logger, block.Slot, block.Body.RandaoReveal.ToString().Substring(0, 10),
                    block, block.Body.Graffiti.ToString().Substring(0, 10), null);
            
            return block;
        }

        private Hash32 ComputeNewStateRoot(BeaconState state, BeaconBlock block)
        {
            _beaconStateTransition.ProcessSlots(state, block.Slot);
            _beaconStateTransition.ProcessBlock(state, block, validateStateRoot: false);
            Hash32 stateRoot = state.HashTreeRoot(_miscellaneousParameterOptions.CurrentValue, _timeParameterOptions.CurrentValue,
                _stateListLengthOptions.CurrentValue, _maxOperationsPerBlockOptions.CurrentValue);
            return stateRoot;
        }
        
        private async Task<ulong> GetPreviousEth1Distance(IStore store, BeaconState state, Hash32 parentRoot)
        {
            TimeParameters timeParameters = _timeParameterOptions.CurrentValue;
            Slot eth1VotingPeriodSlot = new Slot(state.Slot % timeParameters.SlotsPerEth1VotingPeriod);
            Slot startOfEth1VotingPeriodSlot = state.Slot - eth1VotingPeriodSlot;
            Hash32 startOfEth1VotingPeriodSigningRoot = await _forkChoice.GetAncestorAsync(store, parentRoot, startOfEth1VotingPeriodSlot);

            if (startOfEth1VotingPeriodSigningRoot == Hash32.Zero)
            {
                // Don't have blocks for slot yet
                // i.e. parent/head < eth1 vote start for new block < slot new block is for
                // This is supposed to be caught by the tail check, need to get at least sqrt(eth1 vote period) into new cycle
                // But may not happen in test environment, so use parent's distance (or could use default, i.e. follow distance * 2)
                if (_logger.IsWarn())
                    Log.NoBlocksSinceEth1VotingPeriodDefaulting(_logger, state.Slot, eth1VotingPeriodSlot, parentRoot,
                        startOfEth1VotingPeriodSlot, null);
                startOfEth1VotingPeriodSigningRoot = parentRoot;
            }

            BeaconState startOfEth1VotingPeriodState =
                await store.GetBlockStateAsync(startOfEth1VotingPeriodSigningRoot).ConfigureAwait(false);
            Hash32 startOfEth1VotingPeriodBlockHash = startOfEth1VotingPeriodState.Eth1Data.BlockHash;
            ulong distance = await _eth1DataProvider.GetDistanceAsync(startOfEth1VotingPeriodBlockHash).ConfigureAwait(false);
            return distance;
        }

        private async Task<Eth1Data> GetEth1VoteAsync(BeaconState state, IStore store, Hash32 parentRoot)
        {
            TimeParameters timeParameters = _timeParameterOptions.CurrentValue;
            HonestValidatorConstants honestValidatorConstants = _honestValidatorConstantOptions.CurrentValue;

            ulong eth1VotingPeriodSlot = (ulong)state.Slot % timeParameters.SlotsPerEth1VotingPeriod;
            ulong tailThreshold = ((ulong) timeParameters.SlotsPerEth1VotingPeriod).SquareRoot(); // This is an integer square root
            bool isTailPeriod = eth1VotingPeriodSlot >= tailThreshold;

            ulong maximumDistance;
            if (isTailPeriod)
            {
                ulong previousEth1Distance = await GetPreviousEth1Distance(store, state, parentRoot).ConfigureAwait(false);
                maximumDistance = previousEth1Distance;
            }
            else
            {
                maximumDistance = 2 * honestValidatorConstants.Eth1FollowDistance;
            }
            
            Dictionary<Eth1Data, int> voteCount = state.Eth1DataVotes
                .GroupBy(x => x)
                .ToDictionary(x => x.Key, x => x.Count());

            Eth1Data bestEth1Data = await _eth1DataProvider.GetEth1DataAsync(honestValidatorConstants.Eth1FollowDistance).ConfigureAwait(false);
            if (!voteCount.TryGetValue(bestEth1Data, out int bestEth1DataVotes))
            {
                bestEth1DataVotes = 0;
            }
            
            for (ulong distance = honestValidatorConstants.Eth1FollowDistance + 1; distance < maximumDistance; distance++)
            {
                Eth1Data eth1Data = await _eth1DataProvider.GetEth1DataAsync(distance).ConfigureAwait(false);
                if (voteCount.TryGetValue(eth1Data, out int eth1DataVotes))
                {
                    if (eth1DataVotes > bestEth1DataVotes)
                    {
                        bestEth1Data = eth1Data;
                        bestEth1DataVotes = eth1DataVotes;
                    }
                }
            }

            return bestEth1Data;
        }
    }
}
