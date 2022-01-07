using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;

namespace Nethermind.State.SnapSync
{
    public class AccountRange
    {
        public AccountRange(Keccak rootHash, Keccak startingHash, Keccak limitHash)
        {
            RootHash = rootHash;
            StartingHash = startingHash;
            LimitHash = limitHash;
        }

        public Keccak RootHash { get;}
        public Keccak StartingHash { get; }
        public Keccak LimitHash { get; }
    }
}
