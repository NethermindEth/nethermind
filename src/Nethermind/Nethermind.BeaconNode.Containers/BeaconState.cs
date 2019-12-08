﻿using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Containers
{
    public class BeaconState
    {
        private readonly List<Gwei> _balances;
        private readonly Hash32[] _blockRoots;

        //private readonly Crosslink[] _currentCrosslinks;
        private readonly List<PendingAttestation> _currentEpochAttestations;

        private readonly List<Eth1Data> _eth1DataVotes;
        private readonly List<Hash32> _historicalRoots;

        //private readonly Crosslink[] _previousCrosslinks;
        private readonly List<PendingAttestation> _previousEpochAttestations;

        private readonly Hash32[] _randaoMixes;
        private readonly Gwei[] _slashings;
        private readonly Hash32[] _stateRoots;
        private readonly List<Validator> _validators;

        public BeaconState(
            ulong genesisTime,
            Slot slot,
            Fork fork,
            BeaconBlockHeader latestBlockHeader,
            Hash32[] blockRoots,
            Hash32[] stateRoots,
            IList<Hash32> historicalRoots,
            Eth1Data eth1Data,
            IList<Eth1Data> eth1DataVotes,
            ulong eth1DepositIndex,
            IList<Validator> validators,
            IList<Gwei> balances,
            Hash32[] randaoMixes,
            Gwei[] slashings,
            IList<PendingAttestation> previousEpochAttestations,
            IList<PendingAttestation> currentEpochAttestations,
            BitArray justificationBits,
            Checkpoint previousJustifiedCheckpoint,
            Checkpoint currentJustifiedCheckpoint,
            Checkpoint finalizedCheckpoint)
        {
            GenesisTime = genesisTime;
            Slot = slot;
            Fork = fork;
            LatestBlockHeader = latestBlockHeader;
            _blockRoots = blockRoots;
            _stateRoots = stateRoots;
            _historicalRoots = historicalRoots.ToList();
            Eth1Data = eth1Data;
            _eth1DataVotes = eth1DataVotes.ToList();
            Eth1DepositIndex = eth1DepositIndex;
            _validators = validators.ToList();
            _balances = balances.ToList();
            _randaoMixes = randaoMixes;
            _slashings = slashings;
            _previousEpochAttestations = previousEpochAttestations.ToList();
            _currentEpochAttestations = currentEpochAttestations.ToList();
            JustificationBits = justificationBits;
            PreviousJustifiedCheckpoint = previousJustifiedCheckpoint;
            CurrentJustifiedCheckpoint = currentJustifiedCheckpoint;
            FinalizedCheckpoint = finalizedCheckpoint;
        }

        public BeaconState(ulong genesisTime, ulong eth1DepositIndex, Eth1Data eth1Data, BeaconBlockHeader latestBlockHeader,
            uint slotsPerHistoricalRoot, ulong epochsPerHistoricalVector, ulong epochsPerSlashingsVector, int justificationBitsLength)
        {
            GenesisTime = genesisTime;
            Eth1DepositIndex = eth1DepositIndex;
            _eth1DataVotes = new List<Eth1Data>();
            Eth1Data = eth1Data;
            LatestBlockHeader = latestBlockHeader;
            _validators = new List<Validator>();
            _balances = new List<Gwei>();
            _blockRoots = Enumerable.Repeat(Hash32.Zero, (int)slotsPerHistoricalRoot).ToArray();
            _stateRoots = Enumerable.Repeat(Hash32.Zero, (int)slotsPerHistoricalRoot).ToArray();
            _historicalRoots = new List<Hash32>();
            _randaoMixes = Enumerable.Repeat(Hash32.Zero, (int)epochsPerHistoricalVector).ToArray();
            _slashings = Enumerable.Repeat(Gwei.Zero, (int)epochsPerSlashingsVector).ToArray();
            _previousEpochAttestations = new List<PendingAttestation>();
            _currentEpochAttestations = new List<PendingAttestation>();
            JustificationBits = new BitArray(justificationBitsLength);
            Fork = new Fork(new ForkVersion(), new ForkVersion(), Epoch.Zero);
            CurrentJustifiedCheckpoint = new Checkpoint(new Epoch(0), Hash32.Zero);
            PreviousJustifiedCheckpoint = new Checkpoint(new Epoch(0), Hash32.Zero);
            FinalizedCheckpoint = new Checkpoint(new Epoch(0), Hash32.Zero);
            //_previousCrosslinks = Enumerable.Repeat(new Crosslink(Shard.Zero), (int)(ulong)shardCount).ToArray();
            //_currentCrosslinks = Enumerable.Repeat(new Crosslink(Shard.Zero), (int)(ulong)shardCount).ToArray();
            //_currentCrosslinks = Enumerable.Range(0, (int)(ulong)shardCount).Select(x => new Crosslink(new Shard((ulong)x))).ToArray();
        }

        public IReadOnlyList<Gwei> Balances { get { return _balances; } }

        public IReadOnlyList<Hash32> BlockRoots { get { return _blockRoots; } }

        public IReadOnlyList<PendingAttestation> CurrentEpochAttestations { get { return _currentEpochAttestations; } }

        //public IReadOnlyList<Crosslink> CurrentCrosslinks { get { return _currentCrosslinks; } }
        public Checkpoint CurrentJustifiedCheckpoint { get; private set; }

        public Eth1Data Eth1Data { get; private set; }
        public IReadOnlyList<Eth1Data> Eth1DataVotes => _eth1DataVotes;
        public ulong Eth1DepositIndex { get; private set; }
        public Checkpoint FinalizedCheckpoint { get; private set; }
        public Fork Fork { get; }
        public ulong GenesisTime { get; private set; }
        public IReadOnlyList<Hash32> HistoricalRoots => _historicalRoots;
        public BitArray JustificationBits { get; private set; }

        public BeaconBlockHeader LatestBlockHeader { get; private set; }

        public IReadOnlyList<PendingAttestation> PreviousEpochAttestations { get { return _previousEpochAttestations; } }

        //public IReadOnlyList<Crosslink> PreviousCrosslinks { get { return _previousCrosslinks; } }
        public Checkpoint PreviousJustifiedCheckpoint { get; private set; }

        public IReadOnlyList<Hash32> RandaoMixes { get { return _randaoMixes; } }

        public IReadOnlyList<Gwei> Slashings { get { return _slashings; } }

        public Slot Slot { get; private set; }

        public IReadOnlyList<Hash32> StateRoots { get { return _stateRoots; } }

        //public Shard StartShard { get; }
        public IReadOnlyList<Validator> Validators { get { return _validators; } }

        /// <summary>
        /// Creates a deep copy of the object.
        /// </summary>
        public static BeaconState Clone(BeaconState other)
        {
            var clone = new BeaconState(
                other.GenesisTime,
                other.Slot,
                other.Fork,
                BeaconBlockHeader.Clone(other.LatestBlockHeader),
                other.BlockRoots.ToArray(),
                other.StateRoots.ToArray(),
                other.HistoricalRoots.ToArray(),
                Eth1Data.Clone(other.Eth1Data),
                other.Eth1DataVotes.Select(x => Eth1Data.Clone(x)).ToList(),
                other.Eth1DepositIndex,
                other.Validators.Select(x => Validator.Clone(x)).ToList(),
                other.Balances.Select(x => x).ToList(),
                other.RandaoMixes.Select(x => x).ToArray(),
                other.Slashings.Select(x => x).ToArray(),
                other.PreviousEpochAttestations.Select(x => PendingAttestation.Clone(x)).ToList(),
                other.CurrentEpochAttestations.Select(x => PendingAttestation.Clone(x)).ToList(),
                new BitArray(other.JustificationBits),
                Checkpoint.Clone(other.PreviousJustifiedCheckpoint),
                Checkpoint.Clone(other.CurrentJustifiedCheckpoint),
                Checkpoint.Clone(other.FinalizedCheckpoint)
                );
            return clone;
        }

        public void AddCurrentAttestation(PendingAttestation attestation) => _currentEpochAttestations.Add(attestation);

        public void AddEth1DataVote(Eth1Data eth1Data) => _eth1DataVotes.Add(eth1Data);

        public void AddHistoricalRoot(Hash32 historicalRoot) => _historicalRoots.Add(historicalRoot);

        public void AddPreviousAttestation(PendingAttestation attestation) => _previousEpochAttestations.Add(attestation);

        public void AddValidatorWithBalance(Validator validator, Gwei amount)
        {
            _validators.Add(validator);
            _balances.Add(amount);
        }

        public void SetGenesisTime(ulong genesisTime) => GenesisTime = genesisTime;

        public void ClearEth1DataVotes() => _eth1DataVotes.Clear();

        public void IncreaseEth1DepositIndex() => Eth1DepositIndex++;

        public void IncreaseSlot()
        {
            Slot = (Slot)(Slot + 1UL);
        }

        public void JustificationBitsShift()
        {
            // state.justification_bits[1:] = state.justification_bits[:-1]
            // Treated as little endian, so left shift sets new bit 1,to old bit 0, new bit 2 to old bit 1, etc
            JustificationBits.LeftShift(1);
        }

        public void SetBalance(ValidatorIndex validatorIndex, Gwei balance) => _balances[(int)validatorIndex] = balance;

        public void SetBlockRoot(Slot index, Hash32 blockRoot) => _blockRoots[index] = blockRoot;

        public void SetCurrentEpochAttestations(IReadOnlyList<PendingAttestation> attestations)
        {
            _currentEpochAttestations.Clear();
            _currentEpochAttestations.AddRange(attestations);
        }

        public void SetCurrentJustifiedCheckpoint(Checkpoint checkpoint) => CurrentJustifiedCheckpoint = checkpoint;

        public void SetEth1Data(Eth1Data eth1Data) => Eth1Data = eth1Data;

        public void SetEth1DepositIndex(ulong value) => Eth1DepositIndex = value;

        public void SetFinalizedCheckpoint(Checkpoint checkpoint) => FinalizedCheckpoint = checkpoint;

        public void SetJustificationBits(BitArray justificationBits)
        {
            JustificationBits.SetAll(false);
            JustificationBits.Or(justificationBits);
        }

        public void SetLatestBlockHeader(BeaconBlockHeader blockHeader) => LatestBlockHeader = blockHeader;

        public void SetPreviousEpochAttestations(IReadOnlyList<PendingAttestation> attestations)
        {
            _previousEpochAttestations.Clear();
            _previousEpochAttestations.AddRange(attestations);
        }

        public void SetPreviousJustifiedCheckpoint(Checkpoint checkpoint) => PreviousJustifiedCheckpoint = checkpoint;

        public void SetRandaoMix(Epoch randaoIndex, Hash32 mix) => _randaoMixes[randaoIndex] = mix;

        public void SetSlashings(Epoch slashingsIndex, Gwei amount) => _slashings[slashingsIndex] += amount;

        public void SetSlot(Slot slot) => Slot = slot;

        public void SetStateRoot(Slot index, Hash32 stateRoot) => _stateRoots[index] = stateRoot;

        public override string ToString() => $"G:{GenesisTime} S:{Slot} F:({Fork})";
    }
}
