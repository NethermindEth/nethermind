using System;
using System.Collections.Generic;
using System.Linq;

using Epoch = System.UInt64;
using ValidatorIndex = System.UInt64;

namespace Cortex.Containers
{
    public class BeaconState
    {
        public BeaconState(ulong genesisTime, Eth1Data eth1Data, BeaconBlockHeader latestBlockHeader)
        {
            GenesisTime = genesisTime;
            Eth1Data = eth1Data;
            LatestBlockHeader = latestBlockHeader;
        }

        public ulong GenesisTime { get; }

        public BeaconBlockHeader LatestBlockHeader { get; }

        public IList<Validator> Validators { get; }

        public Eth1Data Eth1Data { get; }

        /// <summary>
        /// Return the sequence of active validator indices at ``epoch``.
        /// </summary>
        public IList<ValidatorIndex> GetActiveValidatorIndices(Epoch epoch)
        {
            return Validators
                .Select((validator, index) => new { validator, index })
                .Where(x => x.validator.IsActiveValidator(epoch))
                .Select(x => (ValidatorIndex)x.index)
                .ToList();
        }
    }
}
