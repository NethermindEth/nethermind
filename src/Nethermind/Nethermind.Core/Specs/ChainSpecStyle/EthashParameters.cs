using System.Collections.Generic;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Core.Specs.ChainSpecStyle
{
    public class EthashParameters
    {
        public UInt256 MinimumDifficulty { get; set; }

        public long DifficultyBoundDivisor { get; set; }

        public long DurationLimit { get; set; }

        // why is it here??? (this is what chainspec does)
        public long HomesteadTransition { get; set; }

        public long? DaoHardforkTransition { get; set; }

        /// <summary>
        /// This is stored in the Nethermind.Blockchain.DaoData class instead.
        /// </summary>
        public Address DaoHardforkBeneficiary { get; set; }

        /// <summary>
        /// This is stored in the Nethermind.Blockchain.DaoData class instead.
        /// </summary>
        public Address[] DaoHardforkAccounts { get; set; }

        public long Eip100bTransition { get; set; }

        public Dictionary<long, UInt256> BlockRewards { get; set; }

        public Dictionary<long, long> DifficultyBombDelays { get; set; }
    }
}