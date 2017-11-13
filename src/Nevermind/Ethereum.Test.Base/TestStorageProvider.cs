using System.Collections.Generic;
using System.Linq;
using Nevermind.Core;
using Nevermind.Evm;
using Nevermind.Store;

namespace Ethereum.Test.Base
{
    public class TestStorageProvider : IStorageProvider
    {
        private readonly InMemoryDb _db;

        private readonly Dictionary<Address, StorageTree> _storages = new Dictionary<Address, StorageTree>();

        public TestStorageProvider(InMemoryDb db)
        {
            _db = db;
        }

        public StorageTree GetOrCreateStorage(Address address)
        {
            if (!_storages.ContainsKey(address))
            {
                _storages[address] = new StorageTree(_db);
            }

            return GetStorage(address);
        }

        public StorageTree GetStorage(Address address)
        {
            return _storages[address];
        }

        public Dictionary<Address, StateSnapshot> TakeSnapshot()
        {
            return _storages.ToDictionary(s => s.Key, s => s.Value.TakeSnapshot());
        }

        public void Restore(Dictionary<Address, StateSnapshot> snapshot)
        {
            _storages.Clear();
            foreach (KeyValuePair<Address, StateSnapshot> snapByAddress in snapshot)
            {
                _storages[snapByAddress.Key] = new StorageTree(snapByAddress.Value); 
            }
        }
    }
}