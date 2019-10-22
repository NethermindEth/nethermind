using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace Cortex.Containers
{
    public class BeaconState
    {
        private readonly List<Gwei> _balances;
        private readonly Hash32[] _blockRoots;
        private readonly Crosslink[] _currentCrosslinks;
        private readonly List<PendingAttestation> _currentEpochAttestations;
        private readonly Crosslink[] _previousCrosslinks;
        private readonly List<PendingAttestation> _previousEpochAttestations;
        private readonly Hash32[] _randaoMixes;
        private readonly Hash32[] _stateRoots;
        private readonly List<Validator> _validators;

        public BeaconState(ulong genesisTime, ulong eth1DepositIndex, Eth1Data eth1Data, BeaconBlockHeader latestBlockHeader, Slot slotsPerHistoricalRoot, Epoch epochsPerHistoricalVector, int justificationBitsLength, Shard shardCount)
        {
            GenesisTime = genesisTime;
            Eth1DepositIndex = eth1DepositIndex;
            Eth1Data = eth1Data;
            LatestBlockHeader = latestBlockHeader;
            _validators = new List<Validator>();
            _balances = new List<Gwei>();
            _blockRoots = Enumerable.Repeat(Hash32.Zero, (int)(ulong)slotsPerHistoricalRoot).ToArray();
            _stateRoots = Enumerable.Repeat(Hash32.Zero, (int)(ulong)slotsPerHistoricalRoot).ToArray();
            _randaoMixes = Enumerable.Repeat(Hash32.Zero, (int)(ulong)epochsPerHistoricalVector).ToArray();
            _previousEpochAttestations = new List<PendingAttestation>();
            _currentEpochAttestations = new List<PendingAttestation>();
            JustificationBits = new BitArray(justificationBitsLength);
            Fork = new Fork();
            _previousCrosslinks = Enumerable.Repeat(new Crosslink(Shard.Zero), (int)(ulong)shardCount).ToArray();
            _currentCrosslinks = Enumerable.Repeat(new Crosslink(Shard.Zero), (int)(ulong)shardCount).ToArray();
            //_currentCrosslinks = Enumerable.Range(0, (int)(ulong)shardCount).Select(x => new Crosslink(new Shard((ulong)x))).ToArray();
        }

        public IReadOnlyList<Gwei> Balances { get { return _balances; } }

        public IReadOnlyList<Hash32> BlockRoots { get { return _blockRoots; } }

        public IReadOnlyList<Crosslink> CurrentCrosslinks { get { return _currentCrosslinks; } }

        public IReadOnlyList<PendingAttestation> CurrentEpochAttestations { get { return _currentEpochAttestations; } }

        public Checkpoint? CurrentJustifiedCheckpoint { get; private set; }

        public Eth1Data Eth1Data { get; }

        public ulong Eth1DepositIndex { get; private set; }

        public Checkpoint? FinalizedCheckpoint { get; private set; }

        public Fork Fork { get; }

        public ulong GenesisTime { get; }

        public BitArray JustificationBits { get; private set; }

        public BeaconBlockHeader LatestBlockHeader { get; }

        public IReadOnlyList<Crosslink> PreviousCrosslinks { get { return _previousCrosslinks; } }

        public IReadOnlyList<PendingAttestation> PreviousEpochAttestations { get { return _previousEpochAttestations; } }

        public Checkpoint? PreviousJustifiedCheckpoint { get; private set; }

        public IReadOnlyList<Hash32> RandaoMixes { get { return _randaoMixes; } }

        public Slot Slot { get; private set; }

        public Shard StartShard { get; }

        public IReadOnlyList<Hash32> StateRoots { get { return _stateRoots; } }

        public IReadOnlyList<Validator> Validators { get { return _validators; } }

        public void AddCurrentAttestation(PendingAttestation attestation)
        {
            _currentEpochAttestations.Add(attestation);
        }

        public void AddPreviousAttestation(PendingAttestation attestation)
        {
            _previousEpochAttestations.Add(attestation);
        }

        public void AddValidatorWithBalance(Validator validator, Gwei amount)
        {
            _validators.Add(validator);
            _balances.Add(amount);
        }

        /// <summary>
        /// Return the sequence of active validator indices at ``epoch``.
        /// </summary>
        public IList<ValidatorIndex> GetActiveValidatorIndices(Epoch epoch)
        {
            return Validators
                .Select((validator, index) => new { validator, index })
                .Where(x => x.validator.IsActiveValidator(epoch))
                .Select(x => (ValidatorIndex)(ulong)x.index)
                .ToList();
        }

        /// <summary>
        /// Increase the validator balance at index 'index' by 'delta'.
        /// </summary>
        public void IncreaseBalanceForValidator(ValidatorIndex index, Gwei amount)
        {
            // TODO: Would a dictionary be better, to handle ulong index?
            var arrayIndex = (int)(ulong)index;
            var balance = _balances[arrayIndex];
            balance += amount;
            _balances[arrayIndex] = balance;
        }

        public void IncreaseEth1DepositIndex()
        {
            Eth1DepositIndex++;
        }

        public void IncreaseSlot()
        {
            Slot = new Slot((ulong)Slot + 1);
        }

        public void JustificationBitsShift()
        {
            // state.justification_bits[1:] = state.justification_bits[:-1]
            // Treated as little endian, so left shift sets new bit 1,to old bit 0, new bit 2 to old bit 1, etc
            JustificationBits.LeftShift(1);
        }

        public void SetBlockRoot(Slot index, Hash32 blockRoot)
        {
            _blockRoots[(int)(ulong)index] = blockRoot;
        }

        public void SetCurrentJustifiedCheckpoint(Checkpoint checkpoint)
        {
            CurrentJustifiedCheckpoint = checkpoint;
        }

        public void SetFinalizedCheckpoint(Checkpoint checkpoint)
        {
            FinalizedCheckpoint = checkpoint;
        }

        public void SetJustificationBits(BitArray justificationBits)
        {
            JustificationBits.SetAll(false);
            JustificationBits.Or(justificationBits);
        }

        public void SetPreviousJustifiedCheckpoint(Checkpoint checkpoint)
        {
            PreviousJustifiedCheckpoint = checkpoint;
        }

        public void SetSlot(Slot slot)
        {
            Slot = slot;
        }

        public void SetStateRoot(Slot index, Hash32 stateRoot)
        {
            _stateRoots[(int)(ulong)index] = stateRoot;
        }

        public override string ToString()
        {
            return $"G:{GenesisTime} S:{Slot} F:({Fork})";
        }
    }
}
