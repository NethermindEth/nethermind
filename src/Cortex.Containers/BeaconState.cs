using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace Cortex.Containers
{
    public class BeaconState
    {
        private readonly List<Gwei> _balances;
        private readonly Hash32[] _blockRoots;
        private readonly List<PendingAttestation> _currentEpochAttestations;
        private readonly List<PendingAttestation> _previousEpochAttestations;
        private readonly Hash32[] _randaoMixes;
        private readonly List<Validator> _validators;

        public BeaconState(ulong genesisTime, ulong eth1DepositIndex, Eth1Data eth1Data, BeaconBlockHeader latestBlockHeader, Slot slotsPerHistoricalRoot, Epoch epochsPerHistoricalVector)
        {
            GenesisTime = genesisTime;
            Eth1DepositIndex = eth1DepositIndex;
            Eth1Data = eth1Data;
            LatestBlockHeader = latestBlockHeader;
            _validators = new List<Validator>();
            _balances = new List<Gwei>();
            _blockRoots = Enumerable.Repeat(new Hash32(), (int)(ulong)slotsPerHistoricalRoot).ToArray();
            _randaoMixes = Enumerable.Repeat(new Hash32(), (int)(ulong)epochsPerHistoricalVector).ToArray();
            _previousEpochAttestations = new List<PendingAttestation>();
            _currentEpochAttestations = new List<PendingAttestation>();
        }

        public IReadOnlyList<Gwei> Balances { get { return _balances; } }

        public IReadOnlyList<Hash32> BlockRoots { get { return _blockRoots; } }

        public IReadOnlyList<PendingAttestation> CurrentEpochAttestations { get { return _currentEpochAttestations; } }

        public Checkpoint CurrentJustifiedCheckpoint { get; private set; }

        public Eth1Data Eth1Data { get; }

        public ulong Eth1DepositIndex { get; private set; }

        public Checkpoint FinalizedCheckpoint { get; }

        public Fork Fork { get; }

        public ulong GenesisTime { get; }
        public BitArray JustificationBits { get; private set; }

        public BeaconBlockHeader LatestBlockHeader { get; }

        public IReadOnlyList<PendingAttestation> PreviousEpochAttestations { get { return _previousEpochAttestations; } }

        public Checkpoint PreviousJustifiedCheckpoint { get; private set; }

        public IReadOnlyList<Hash32> RandaoMixes { get { return _randaoMixes; } }

        public Slot Slot { get; private set; }

        public Shard StartShard { get; }

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

        public void SetBlockRoot(Slot slotIndex, Hash32 root)
        {
            _blockRoots[(int)(ulong)slotIndex] = root;
        }

        public void SetCurrentJustifiedCheckpoint(Checkpoint checkpoint)
        {
            PreviousJustifiedCheckpoint = CurrentJustifiedCheckpoint;
            CurrentJustifiedCheckpoint = checkpoint;
        }

        public void SetJustificationBits(BitArray justificationBits)
        {
            JustificationBits = justificationBits;
        }

        public void SetSlot(Slot slot)
        {
            Slot = slot;
        }

        public override string ToString()
        {
            return $"G:{GenesisTime} S:{Slot} F:({Fork})";
        }
    }
}
