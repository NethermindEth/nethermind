using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nethermind.BeaconNode.Configuration;
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
        private readonly IOptionsMonitor<MaxOperationsPerBlock> _maxOperationsPerBlockOptions;
        private readonly IOptionsMonitor<HonestValidatorConstants> _honestValidatorConstantOptions;
        private readonly BeaconStateTransition _beaconStateTransition;
        private readonly ForkChoice _forkChoice;
        private readonly IStoreProvider _storeProvider;
        private readonly IEth1DataProvider _eth1DataProvider;

        public BlockProducer(ILogger<BlockProducer> logger, 
            IOptionsMonitor<MiscellaneousParameters> miscellaneousParameterOptions,
            IOptionsMonitor<TimeParameters> timeParameterOptions,
            IOptionsMonitor<MaxOperationsPerBlock> maxOperationsPerBlockOptions,
            IOptionsMonitor<HonestValidatorConstants> honestValidatorConstantOptions,
            BeaconStateTransition beaconStateTransition,
            ForkChoice forkChoice, 
            IStoreProvider storeProvider,
            IEth1DataProvider eth1DataProvider)
        {
            _logger = logger;
            _miscellaneousParameterOptions = miscellaneousParameterOptions;
            _timeParameterOptions = timeParameterOptions;
            _maxOperationsPerBlockOptions = maxOperationsPerBlockOptions;
            _honestValidatorConstantOptions = honestValidatorConstantOptions;
            _beaconStateTransition = beaconStateTransition;
            _forkChoice = forkChoice;
            _storeProvider = storeProvider;
            _eth1DataProvider = eth1DataProvider;
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
            Hash32 head = await _forkChoice.GetHeadAsync(store);
            if (!store.TryGetBlock(head, out BeaconBlock? headBlock))
            {
                throw new Exception($"Cannot find block for head root {head}");
            }

            BeaconBlock parentBlock;
            BeaconState? retrievedParentState;
            if (headBlock!.Slot > previousSlot)
            {
                // Requesting a block for a past slot?
                Hash32 ancestorSigningRoot = _forkChoice.GetAncestor(store, head, previousSlot);
                if (!store.TryGetBlock(ancestorSigningRoot, out BeaconBlock? retrievedParentBlock))
                {
                    throw new Exception($"Cannot find block for ancestor signing root {ancestorSigningRoot}");
                }
                parentBlock = retrievedParentBlock!;
                if (!store.TryGetBlockState(ancestorSigningRoot, out retrievedParentState))
                {
                    throw new Exception($"Cannot find state for ancestor signing root {ancestorSigningRoot}");
                }
            }
            else
            {
                parentBlock = headBlock!;
                if (parentBlock.Slot < previousSlot)
                {
                    if (_logger.IsDebug()) Log.NewBlockSkippedSlots(_logger, slot, randaoReveal, parentBlock.Slot, null);
                }
                if (!store.TryGetBlockState(head, out retrievedParentState))
                {
                    throw new Exception($"Cannot find state for head signing root {head}");
                }
            }

            // Clone state (will mutate) and process outstanding slots
            BeaconState state = BeaconState.Clone(retrievedParentState!);
            _beaconStateTransition.ProcessSlots(state, slot);

            // TODO: Is this the same as head / ancestorSigningRoot above?
            Hash32 parentRoot = parentBlock.HashTreeRoot(_miscellaneousParameterOptions.CurrentValue, _maxOperationsPerBlockOptions.CurrentValue);
            
            ulong previousEth1Distance = GetPreviousEth1Distance(store, state, parentRoot);
            Eth1Data eth1Vote = await GetEth1VoteAsync(state, previousEth1Distance);

            Bytes32 graffiti = new Bytes32();

            IEnumerable<ProposerSlashing> proposerSlashings = Array.Empty<ProposerSlashing>();
            IEnumerable<AttesterSlashing> attesterSlashings = Array.Empty<AttesterSlashing>();
            IEnumerable<Attestation> attestations = Array.Empty<Attestation>();
            IEnumerable<Deposit> deposits = Array.Empty<Deposit>();
            IEnumerable<VoluntaryExit> voluntaryExits = Array.Empty<VoluntaryExit>();

            BeaconBlockBody body = new BeaconBlockBody(randaoReveal, eth1Vote, graffiti, proposerSlashings,
                attesterSlashings, attestations, deposits, voluntaryExits);
            
            Hash32 stateRoot = Hash32.Zero;

            BeaconBlock block = new BeaconBlock(slot, parentRoot, stateRoot, body, BlsSignature.Empty);

            // new block = slot, parent root,
            //  signature = null
            //  body = assemble body

            // assemble body:
            //  from opPool get proposer slashings, attester slashings, attestations, voluntary exits
            //  get eth1data
            // generate deposits (based on new data)
            //     -> if eth1data deposit count > state deposit index, then get from op pool, sort and calculate merkle tree
            // deposit root = merkleRoot
            // randaoReveal

            // apply block to state transition
            // block.stateRoot = new state hash

            return await Task.Run(() => block);
        }

        private ulong GetPreviousEth1Distance(IStore store, BeaconState state, Hash32 parentRoot)
        {
            TimeParameters timeParameters = _timeParameterOptions.CurrentValue;
            Slot eth1VotingPeriodSlot = new Slot(state.Slot % timeParameters.SlotsPerEth1VotingPeriod);
            Slot startOfEth1VotingPeriodSlot = state.Slot - eth1VotingPeriodSlot;
            Hash32 startOfEth1VotingPeriodSigningRoot = _forkChoice.GetAncestor(store, parentRoot, startOfEth1VotingPeriodSlot);
            if (!store.TryGetBlockState(startOfEth1VotingPeriodSigningRoot,
                out BeaconState? startOfEth1VotingPeriodState))
            {
                throw new Exception($"Could not find start of Eth1 voting period state {startOfEth1VotingPeriodSigningRoot}.");
            }

            Hash32 startOfEth1VotingPeriodBlockHash = startOfEth1VotingPeriodState!.Eth1Data.BlockHash;
            ulong distance = _eth1DataProvider.GetDistance(startOfEth1VotingPeriodBlockHash);
            return distance;
        }

        private async Task<Eth1Data> GetEth1VoteAsync(BeaconState state, ulong previousEth1Distance)
        {
            TimeParameters timeParameters = _timeParameterOptions.CurrentValue;
            HonestValidatorConstants honestValidatorConstants = _honestValidatorConstantOptions.CurrentValue;

            ulong eth1VotingPeriodSlot = (ulong)state.Slot % timeParameters.SlotsPerEth1VotingPeriod;
            ulong tailThreshold = ((ulong) timeParameters.SlotsPerEth1VotingPeriod).SquareRoot(); // This is an integer square root
            bool isTailPeriod = eth1VotingPeriodSlot >= tailThreshold;

            ulong maximumDistance;
            if (isTailPeriod)
            {
                maximumDistance = previousEth1Distance;
            }
            else
            {
                maximumDistance = 2 * honestValidatorConstants.Eth1FollowDistance;
            }
            
            Dictionary<Eth1Data, int> voteCount = state.Eth1DataVotes
                .GroupBy(x => x)
                .ToDictionary(x => x.Key, x => x.Count());

            Eth1Data bestEth1Data = await _eth1DataProvider.GetEth1DataAsync(honestValidatorConstants.Eth1FollowDistance);
            if (!voteCount.TryGetValue(bestEth1Data, out int bestEth1DataVotes))
            {
                bestEth1DataVotes = 0;
            }
            
            for (ulong distance = honestValidatorConstants.Eth1FollowDistance + 1; distance < maximumDistance; distance++)
            {
                Eth1Data eth1Data = await _eth1DataProvider.GetEth1DataAsync(distance);
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
