using System.Collections.Concurrent;
using Nethermind.Core.Crypto;
using Nethermind.Mev.Data;

namespace Nethermind.Mev.Source
{
    public class BundleWithHashes
    {
        public BundleWithHashes(MevBundle bundle)
        {
            Bundle = bundle;
            BlockHashes = new ConcurrentBag<Keccak>();
        }

        public MevBundle Bundle { get; }
        public ConcurrentBag<Keccak> BlockHashes { get; }
            
            
        public static implicit operator MevBundle(BundleWithHashes bundle) => bundle.Bundle;
    }
}
