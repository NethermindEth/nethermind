using Nethermind.Core;

namespace Nethermind.Store
{
    public interface IDbProvider : ISnapshotable
    {
        IDb GetOrCreateStateDb();
        IDb GetOrCreateStorageDb(Address address);
        IDb GetOrCreateCodeDb();
    }
}