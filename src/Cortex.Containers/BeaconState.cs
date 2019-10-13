using System.Collections.Generic;
using System.Linq;

namespace Cortex.Containers
{
    public class BeaconState
    {
        private readonly List<Gwei> _balances;
        private readonly List<Validator> _validators;

        public BeaconState(ulong genesisTime, Eth1Data eth1Data, BeaconBlockHeader latestBlockHeader)
        {
            Eth1Data = eth1Data;
            GenesisTime = genesisTime;
            LatestBlockHeader = latestBlockHeader;
            _validators = new List<Validator>();
            _balances = new List<Gwei>();
        }

        public IReadOnlyList<Gwei> Balances { get { return _balances; } }

        public Eth1Data Eth1Data { get; }

        public ulong Eth1DepositIndex { get; private set; }

        public ulong GenesisTime { get; }

        public BeaconBlockHeader LatestBlockHeader { get; }

        public IReadOnlyList<Validator> Validators { get { return _validators; } }

        public void AddValidator(Validator validator)
        {
            _validators.Add(validator);
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
        public void IncreaseBalance(ValidatorIndex index, Gwei amount)
        {
            // TODO: Would a dictionary be better, to handle ulong index?
            var arrayIndex = (int)(ulong)index;
            if (_balances.Count <= arrayIndex)
            {
                _balances.AddRange(Enumerable.Repeat(new Gwei(), arrayIndex - _balances.Count + 1));
            }
            var balance = _balances[arrayIndex];
            balance += amount;
            _balances[arrayIndex] = balance;
        }

        public void IncreaseEth1DepositIndex()
        {
            Eth1DepositIndex++;
        }
    }
}
