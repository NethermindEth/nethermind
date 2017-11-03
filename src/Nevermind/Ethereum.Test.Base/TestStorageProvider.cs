using System.Collections.Generic;
using Nevermind.Core;
using Nevermind.Evm;
using Nevermind.Store;

namespace Ethereum.VM.Test
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

        public StateSnapshot TakeSnapshot(Address address)
        {
            return _storages.ContainsKey(address) ? _storages[address].TakeSnapshot() : null;
        }

        public void Restore(Address address, StateSnapshot snapshot)
        {
            if (snapshot == null)
            {
                _storages[address] = null;
            }
            else
            {
                _storages[address].Restore(snapshot);
            }
        }
    }
}