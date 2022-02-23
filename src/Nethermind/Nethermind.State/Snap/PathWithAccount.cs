using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.State.Snap
{
    public class PathWithAccount
    {
        public PathWithAccount() { }

        public PathWithAccount(Keccak addressHash, Account account)
        {
            AddressHash = addressHash;
            Account = account;
        }

        public Keccak AddressHash { get; set; }
        public Account Account { get; set; }
    }
}
