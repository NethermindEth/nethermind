using Nevermind.Core;
using Nevermind.Store;

namespace Nevermind.Evm
{
    public interface IStorageProvider
    {
        StorageTree GetOrCreateStorage(Address address);
    }
}