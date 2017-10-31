using Nevermind.Core;
using Nevermind.Store;

namespace Nevermind.Evm
{
    public interface IStorageProvider
    {
        StorageTree GetOrCreateStorage(Address address);

        StateSnapshot TakeSnapshot(Address address);

        void Restore(Address address, StateSnapshot snapshot);
    }
}