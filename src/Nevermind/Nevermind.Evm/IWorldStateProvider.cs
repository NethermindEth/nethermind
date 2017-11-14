using System.Numerics;
using Nevermind.Core;
using Nevermind.Core.Encoding;
using Nevermind.Store;

namespace Nevermind.Evm
{
    public interface IWorldStateProvider
    {
        StateTree State { get; }

        void DeleteAccount(Address address);

        void CreateAccount(Address address, BigInteger balance);

        bool AccountExists(Address address);

        bool IsEmptyAccount(Address address);

        BigInteger GetNonce(Address address);

        BigInteger GetBalance(Address address);

        byte[] GetCode(Keccak codeHash);

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