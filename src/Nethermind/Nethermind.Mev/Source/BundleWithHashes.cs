using System;
using System.Collections.Concurrent;
using Nethermind.Core.Crypto;
using Nethermind.Mev.Data;

namespace Nethermind.Mev.Source
{
    public class BundleWithHashes: IComparable<BundleWithHashes>
    {
        public BundleWithHashes(MevBundle bundle)
        {
            Bundle = bundle;
            BlockHashes = new ConcurrentBag<Keccak>();
        }

        public MevBundle Bundle { get; }
        public ConcurrentBag<Keccak> BlockHashes { get; }


        public static implicit operator MevBundle(BundleWithHashes bundle) => bundle.Bundle;
        public int CompareTo(BundleWithHashes? other)
        {
            CompareBundleWithHashesByBlock bundleWithHashesByBlock = new();
            long BestBlockNumber = bundleWithHashesByBlock.BestBlockNumber;
            if (this.Bundle.BlockNumber == other!.Bundle.BlockNumber)
            {
                return this.Bundle.MinTimestamp.CompareTo(other.Bundle.MinTimestamp);
            }
            else if (this.Bundle.BlockNumber > BestBlockNumber && other.Bundle.BlockNumber > BestBlockNumber)
            {
                return this.Bundle.BlockNumber.CompareTo(other.Bundle.BlockNumber);
            }
            else
            {
                return other.Bundle.BlockNumber.CompareTo(this.Bundle.BlockNumber);
            }
        }
    }
}
