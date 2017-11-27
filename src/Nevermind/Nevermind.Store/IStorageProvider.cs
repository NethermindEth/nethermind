using System.Numerics;
using Nevermind.Core;
using Nevermind.Core.Crypto;
using Nevermind.Core.Encoding;

namespace Nevermind.Store
{
    public interface IStorageProvider : ISnapshotable
    {
        byte[] Get(Address address, BigInteger index);

        void Set(Address address, BigInteger index, byte[] newValue);

        Keccak GetRoot(Address address);
        
        void ClearCaches(); // TODO: temp while designing DB <-> store interaction
    }
}