using System.Collections.Generic;
using Nevermind.Core;
using Nevermind.Store;

namespace Nevermind.Evm
{
    public interface IStorageProvider
    {
        StorageTree GetOrCreateStorage(Address address);

        Dictionary<Address, StateSnapshot> TakeSnapshot();

        void Restore(Dictionary<Address, StateSnapshot> storageSnapshot);
    }
}