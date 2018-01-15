using System.Collections.Generic;
using Nevermind.Core;
using Nevermind.Core.Crypto;
using Nevermind.Utils.Model;

namespace Nevermind.KeyStore
{
    public interface IKeyStore
    {
        (PrivateKey, Result) GetKey(Address address, string password);
        (IEnumerable<Address>, Result) GetKeyAddresses();
        (PrivateKey, Result) GenerateKey(string password);
        Result StoreKey(PrivateKey key, string password);
        Result DeleteKey(Address address, string password);
        int Version { get; }
        int CryptoVersion { get; }
    }
}
