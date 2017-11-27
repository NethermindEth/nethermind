using System.Collections.Generic;
using Nevermind.Core;

namespace Nevermind.Store
{
    public class MultiDb : IMultiDb
    {
        private readonly List<IDb> _dbs = new List<IDb>();
        private readonly ILogger _logger;

        private readonly Stack<Dictionary<IDb, int>> _snapshots = new Stack<Dictionary<IDb, int>>();

        public MultiDb(ILogger logger)
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
            foreach (IDb db in _dbs)
            {
                db.Restore(dbSnapshots.ContainsKey(db) ? dbSnapshots[db] : -1);
            }
        }

        public void Commit()
        {
            _logger?.Log("COMMITING ALL DBS");

            foreach (IDb db in _dbs)
            {
                db.Commit();
            }
        }

        public int TakeSnapshot()
        {
            Dictionary<IDb, int> dbSnapshots = new Dictionary<IDb, int>();
            foreach (IDb db in _dbs)
            {
                dbSnapshots.Add(db, db.TakeSnapshot());
            }

            _snapshots.Push(dbSnapshots);

            int snapshot = _snapshots.Count - 2;
            _logger?.Log($"TAKING DBS SNAPSHOT AT {snapshot}");
            return snapshot;
        }

        public IDb CreateDb()
        {
            IDb db = new InMemoryDb();
            _dbs.Add(db);
            return db;
        }
    }
}