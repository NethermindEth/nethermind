using Nevermind.Core;
using Nevermind.Store;

namespace Nevermind.Evm
{
    public interface IStorageProvider
    {
        StorageTree GetStorage(Address address);
        StorageTree GetOrCreateStorage(Address address);
    }
}