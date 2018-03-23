using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Store
{
    public class DbProvider : IDbProvider
    {
        private readonly IDb _stateDb = new InMemoryDb();
        private readonly IDb _codeDb = new InMemoryDb();
        private readonly Dictionary<Address, IDb> _storageDbs = new Dictionary<Address, IDb>();
        private IEnumerable<IDb> AllDbs
        {
            get
            {
                yield return _stateDb;
                yield return _codeDb;
                foreach (IDb storageDb in _storageDbs.Values)
                {
                    yield return storageDb;
                }
            }
        }
        
        public IDb GetOrCreateStateDb()
        {
            return _stateDb;
        }

        public IDb GetOrCreateStorageDb(Address address)
        {
            if (!_storageDbs.ContainsKey(address))
            {
                _storageDbs[address] = new InMemoryDb();
            }

            return _storageDbs[address];
        }

        public IDb GetOrCreateCodeDb()
        {
            return _codeDb;
        }
        
        private readonly ILogger _logger;

        private readonly Stack<Dictionary<IDb, int>> _snapshots = new Stack<Dictionary<IDb, int>>();

        public DbProvider(ILogger logger)
        {
            _logger = logger;
            _snapshots.Push(new Dictionary<IDb, int>());
        }

        public void Restore(int snapshot)
        {
            _logger?.Log($"RESTORING ALL DBS TO {snapshot}");

            while (_snapshots.Count - 2 != snapshot)
            {
                _snapshots.Pop();
            }

            Dictionary<IDb, int> dbSnapshots = _snapshots.Peek();
            foreach (IDb db in AllDbs)
            {
                db.Restore(dbSnapshots.ContainsKey(db) ? dbSnapshots[db] : -1);
            }
        }

        public void Commit()
        {
            _logger?.Log("COMMITING ALL DBS");

            foreach (IDb db in AllDbs)
            {
                db.Commit();
            }
        }

        public int TakeSnapshot()
        {
            Dictionary<IDb, int> dbSnapshots = new Dictionary<IDb, int>();
            foreach (IDb db in AllDbs)
            {
                dbSnapshots.Add(db, db.TakeSnapshot());
            }

            _snapshots.Push(dbSnapshots);

            int snapshot = _snapshots.Count - 2;
            _logger?.Log($"TAKING DBS SNAPSHOT AT {snapshot}");
            return snapshot;
        }
    }
}