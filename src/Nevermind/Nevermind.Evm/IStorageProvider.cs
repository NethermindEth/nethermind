using System.Numerics;
using Nevermind.Core;
using Nevermind.Core.Encoding;

namespace Nevermind.Evm
{
    public interface IStorageProvider
    {
        byte[] Get(Address address, BigInteger index);

        void Set(Address address, BigInteger index, byte[] newValue);

        Keccak GetRoot(Address address);

        int TakeSnapshot();

        void Restore(int snapshot);

        void Commit(IWorldStateProvider worldStateProvider);
    }
}