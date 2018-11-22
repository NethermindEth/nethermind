using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Wallet
{
    public class HiveWallet : IWallet
    {
        private readonly ISet<Address> _addresses = new HashSet<Address>();

        public void Add(Address address)
            => _addresses.Add(address);

        public Address[] GetAccounts()
            => _addresses.ToArray();

        public void Sign(Transaction tx, int chainId)
        {
            throw new System.NotImplementedException();
        }

        public Signature Sign(Address address, Keccak message)
        {
            throw new System.NotImplementedException();
        }
    }
}