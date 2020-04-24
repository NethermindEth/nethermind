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
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nethermind.BeaconNode.Services;
using Nethermind.Core2;
using Nethermind.Core2.Configuration;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using Nethermind.Logging.Microsoft;

namespace Nethermind.BeaconNode
{
    public class BlockProducer
    {
        private readonly BeaconStateTransition _beaconStateTransition;
        private readonly ICryptographyService _cryptographyService;
        private readonly IEth1DataProvider _eth1DataProvider;
        private readonly IForkChoice _forkChoice;
        private readonly IOptionsMonitor<HonestValidatorConstants> _honestValidatorConstantOptions;
        private readonly ILogger _logger;
        private readonly IOptionsMonitor<MaxOperationsPerBlock> _maxOperationsPerBlockOptions;
        private readonly IOperationPool _operationPool;
        private readonly IStore _store;
        private readonly IOptionsMonitor<TimeParameters> _timeParameterOptions;

        public BlockProducer(ILogger<BlockProducer> logger,
            IOptionsMonitor<TimeParameters> timeParameterOptions,
            IOptionsMonitor<MaxOperationsPerBlock> maxOperationsPerBlockOptions,
            IOptionsMonitor<HonestValidatorConstants> honestValidatorConstantOptions,
            ICryptographyService cryptographyService,
            BeaconStateTransition beaconStateTransition,
            IForkChoice forkChoice,
            IStore store,
            IEth1DataProvider eth1DataProvider,
            IOperationPool operationPool)
        {
            _logger = logger;
            _timeParameterOptions = timeParameterOptions;
            _maxOperationsPerBlockOptions = maxOperationsPerBlockOptions;
            _honestValidatorConstantOptions = honestValidatorConstantOptions;
            _cryptographyService = cryptographyService;
            _beaconStateTransition = beaconStateTransition;
            _forkChoice = forkChoice;
            _store = store;
            _eth1DataProvider = eth1DataProvider;
            _operationPool = operationPool;
        }

        public async Task<BeaconBlock> NewBlockAsync(Slot slot, BlsSignature randaoReveal,
            CancellationToken cancellationToken)
        {
            if (slot == Slot.Zero)
            {
                throw new ArgumentException("Can't generate new block for slot 0, as it is the genesis block.");
            }

            Slot previousSlot = slot - Slot.One;
            Root head = await _forkChoice.GetHeadAsync(_store).ConfigureAwait(false);
            BeaconBlock headBeaconBlock = (await _store.GetSignedBlockAsync(head).ConfigureAwait(false)).Message;

            BeaconState parentState;
            Root parentRoot;
            if (headBeaconBlock!.Slot > previousSlot)
            {
                // Requesting a block for a past slot?
                Root ancestorRoot =
                    await _forkChoice.GetAncestorAsync(_store, head, previousSlot).ConfigureAwait(false);
                parentState = await _store.GetBlockStateAsync(ancestorRoot).ConfigureAwait(false);
                parentRoot = ancestorRoot;
            }
            else
            {
                if (headBeaconBlock.Slot < previousSlot)
                {
                    if (_logger.IsDebug())
                        LogDebug.NewBlockSkippedSlots(_logger, slot, randaoReveal, headBeaconBlock.Slot, null);
                }

                parentState = await _store.GetBlockStateAsync(head).ConfigureAwait(false);
                parentRoot = head;
            }

            // Clone state (will mutate) and process outstanding slots
            BeaconState state = BeaconState.Clone(parentState);
            _beaconStateTransition.ProcessSlots(state, slot);

            MaxOperationsPerBlock maxOperationsPerBlock = _maxOperationsPerBlockOptions.CurrentValue;

            // Eth 1 Data
            Eth1Data eth1Vote = await GetEth1VoteAsync(state, cancellationToken).ConfigureAwait(false);

            List<Deposit> deposits = new List<Deposit>();
            if (eth1Vote.DepositCount > state.Eth1DepositIndex)
            {
                await foreach (Deposit deposit in _eth1DataProvider.GetDepositsAsync(eth1Vote.BlockHash,
                        state.Eth1DepositIndex, maxOperationsPerBlock.MaximumDeposits, cancellationToken)
                    .ConfigureAwait(false))
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

            List<SignedVoluntaryExit> signedVoluntaryExits = new List<SignedVoluntaryExit>();
            await foreach (SignedVoluntaryExit signedVoluntaryExit in _operationPool.GetSignedVoluntaryExits(
                maxOperationsPerBlock.MaximumVoluntaryExits).ConfigureAwait(false))
            {
                signedVoluntaryExits.Add(signedVoluntaryExit);
            }

            // Graffiti
            var graffitiBytes = new byte[32];
            //graffitiBytes[0] = 0x4e; // 'N'
            Bytes32 graffiti = new Bytes32(graffitiBytes);

            // Build block
            BeaconBlockBody body = new BeaconBlockBody(randaoReveal, eth1Vote, graffiti, proposerSlashings,
                attesterSlashings, attestations, deposits, signedVoluntaryExits);
            BeaconBlock block = new BeaconBlock(slot, parentRoot, Root.Zero, body);

            // Apply block to state transition and calculate resulting state root
            Root stateRoot = ComputeNewStateRoot(state, block);
            block.SetStateRoot(stateRoot);

            // Unsigned block

            if (_logger.IsDebug())
                LogDebug.NewBlockProduced(_logger, block.Slot, block.Body.RandaoReveal.ToString().Substring(0, 10),
                    block, block.Body.Graffiti.ToString().Substring(0, 10), null);

            return block;
        }

        private Root ComputeNewStateRoot(BeaconState state, BeaconBlock block)
        {
            _beaconStateTransition.ProcessSlots(state, block.Slot);
            _beaconStateTransition.ProcessBlock(state, block);
            Root stateRoot = _cryptographyService.HashTreeRoot(state);
            return stateRoot;
        }

        private ulong ComputeTimeAtSlot(BeaconState state, Slot slot)
        {
            TimeParameters timeParameters = _timeParameterOptions.CurrentValue;
            return state.GenesisTime + (slot * timeParameters.SecondsPerSlot);
        }

        private async Task<Eth1Data> GetEth1VoteAsync(BeaconState state, CancellationToken cancellationToken)
        {
            // Algorithm in spec:
            // * get all candidate eth1 blocks, between follow distance & 2 * follow distance
            // * filter votes already cast by candidate blocks (e.g. could be on different eth1 fork)
            // * take the highest votes already cast, tiebreak by smallest eth1votes index
            // * if no valid votes, default to most recent candidate, or if no candidates then previous state.eth1_data

            // Note: if no candidates (Eth1 chain not live), then valid votes will be empty

            TimeParameters timeParameters = _timeParameterOptions.CurrentValue;
            HonestValidatorConstants honestValidatorConstants = _honestValidatorConstantOptions.CurrentValue;

            var periodStart = VotingPeriodStartTime(state);

            // `eth1_chain` abstractly represents all blocks in the eth1 chain sorted by ascending block height
            ulong maximumTimestampInclusive = periodStart -
                                              (timeParameters.SecondsPerSlot *
                                               honestValidatorConstants.Eth1FollowDistance);
            ulong minimumTimestampInclusive = periodStart -
                                              (timeParameters.SecondsPerSlot *
                                               honestValidatorConstants.Eth1FollowDistance * 2);
            // is_candidate_block filter passed in as max+min inclusive
            List<Eth1Data> votesToConsider = new List<Eth1Data>();
            await foreach (Eth1Data eth1Data in _eth1DataProvider
                .GetEth1DataDescendingAsync(maximumTimestampInclusive, minimumTimestampInclusive, cancellationToken)
                .ConfigureAwait(false))
            {
                votesToConsider.Add(eth1Data);
            }

            // Valid votes already cast during this period
            IEnumerable<Eth1Data> validVotes = state.Eth1DataVotes
                .Where(x => votesToConsider.Contains((x)));

            // Default vote on latest eth1 block data in the period range unless eth1 chain is not live
            if (votesToConsider.Count == 0)
            {
                return state.Eth1Data;
            }

            Eth1Data? bestEth1Data = validVotes
                .Select((eth1Data, index) => (eth1Data, index))
                .GroupBy(x => x.eth1Data)
                .OrderByDescending(x => x.Count())
                .ThenBy(x => x.First().index)
                .Select(x => x.Key)
                .FirstOrDefault();

            if (bestEth1Data is null)
            {
                return votesToConsider.First();
            }

            return bestEth1Data!;
        }

        private ulong VotingPeriodStartTime(BeaconState state)
        {
            TimeParameters timeParameters = _timeParameterOptions.CurrentValue;
            Slot eth1VotingPeriodStartSlot =
                new Slot(state.Slot - state.Slot % timeParameters.SlotsPerEth1VotingPeriod);
            ulong timeAtSlot = ComputeTimeAtSlot(state, eth1VotingPeriodStartSlot);
            return timeAtSlot;
        }
    }
}