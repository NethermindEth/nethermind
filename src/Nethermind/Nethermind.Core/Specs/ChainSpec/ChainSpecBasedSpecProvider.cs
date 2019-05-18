using System;
using Nethermind.Core.Crypto;

namespace Nethermind.Core.Specs.ChainSpec
{
    public class ChainSpecBasedSpecProvider : ISpecProvider
    {
        private readonly ChainSpec _chainSpec;

        
        
        public ChainSpecBasedSpecProvider(ChainSpec chainSpec)
        {
            _chainSpec = chainSpec ?? throw new ArgumentNullException(nameof(chainSpec));
        }
        
        public IReleaseSpec GenesisSpec { get; }
        
        public IReleaseSpec GetSpec(long blockNumber)
        {
            throw new System.NotImplementedException();
        }

        public long? DaoBlockNumber { get; }
        
        public long PivotBlockNumber { get; }
        
        public Keccak PivotBlockHash { get; }
        
        public int ChainId { get; }
    }
}