using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;

namespace Nethermind.State.Snap
{
    public class AccountRange
    {
        public AccountRange(Keccak rootHash, Keccak startingHash, Keccak limitHash = null, long? blockNumber = null)
        {
            RootHash = rootHash;
            StartingHash = startingHash;
            BlockNumber = blockNumber;
            LimitHash = limitHash;
        }

        public long? BlockNumber { get; }

        /// <summary>
        /// Root hash of the account trie to serve
        /// </summary>
        public Keccak RootHash { get;}

        /// <summary>
        /// Account hash of the first to retrieve
        /// </summary>
        public Keccak StartingHash { get; }

        /// <summary>
        /// Account hash after which to stop serving data
        /// </summary>
        public Keccak? LimitHash { get; }
    }
}
