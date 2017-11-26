using System.Numerics;
using Nevermind.Core;
using Nevermind.Core.Crypto;

namespace Nevermind.Store
{
    public interface IStateProvider
    {
        StateTree State { get; }

        void DeleteAccount(Address address);

        void CreateAccount(Address address, BigInteger balance);

        bool AccountExists(Address address);

        bool IsDeadAccount(Address address);

        bool IsEmptyAccount(Address address);

        BigInteger GetNonce(Address address);

        BigInteger GetBalance(Address address);

        byte[] GetCode(Address address);

        void UpdateCodeHash(Address address, Keccak codeHash);

        void UpdateBalance(Address address, BigInteger balanceChange);

        void UpdateStorageRoot(Address address, Keccak storageRoot);

        void IncrementNonce(Address address);

        Keccak UpdateCode(byte[] code);

        int TakeSnapshot();

        void Restore(int snapshot);

        void Commit();
    }
}