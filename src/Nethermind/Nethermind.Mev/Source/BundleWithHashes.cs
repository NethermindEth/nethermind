using System;
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
        /*public int CompareTo(BundleWithHashes? other)
        {
            if (ReferenceEquals(null, other)) return 1;
 
            long bestBlockNumber = CompareBundleWithHashesByBlock.BestBlockNumber;
            if (this.Bundle.BlockNumber == other.Bundle.BlockNumber)
            {
                return this.Bundle.MinTimestamp.CompareTo(other.Bundle.MinTimestamp);
            }
            else if (this.Bundle.BlockNumber > bestBlockNumber && other.Bundle.BlockNumber > bestBlockNumber)
            {
                return this.Bundle.BlockNumber.CompareTo(other.Bundle.BlockNumber);
            }
            else
            {
                return other.Bundle.BlockNumber.CompareTo(other.Bundle.BlockNumber);
            }
        }*/
    }
}
