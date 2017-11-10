using System.Numerics;
using Nevermind.Core;
using Nevermind.Core.Encoding;
using Nevermind.Store;

namespace Nevermind.Evm
{
    public interface IWorldStateProvider
    {
        StateTree State { get; }

        void CreateAccount(Address address, BigInteger balance);

        //Account GetOrCreateAccount(Address address);

        //void UpdateAccount(Address address, Account account);

        //Account GetAccount(Address address);

        bool AccountExists(Address address);

        BigInteger GetNonce(Address address);

        BigInteger GetBalance(Address address);

        void UpdateCodeHash(Address address, Keccak codeHash);

        void UpdateBalance(Address address, BigInteger balanceChange);

        void UpdateStorageRoot(Address address, Keccak storageRoot);

        void IncrementNonce(Address address);

        Keccak UpdateCode(byte[] code);

        byte[] GetCode(Keccak codeHash);

        byte[] GetCode(Address address);

        void DeleteAccount(Address address);

        StateSnapshot TakeSnapshot();

        void Restore(StateSnapshot snapshot);
    }
}