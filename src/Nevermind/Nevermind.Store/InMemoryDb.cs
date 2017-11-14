using System;
using System.Collections.Generic;
using Nevermind.Core.Encoding;

namespace Nevermind.Store
{
    public class InMemoryDb
    {
        private const int DefaultInitialSize = 4;

        private InMemoryDb(Dictionary<Keccak, byte[]> toCopy)
        {
            _db = new Dictionary<Keccak, byte[]>(toCopy);
        }

        public InMemoryDb()
        {
            _db = new Dictionary<Keccak, byte[]>(DefaultInitialSize);
        }

        private readonly Dictionary<Keccak, byte[]> _db;

        public byte[] this[Keccak key]
        {
            get => _db[key];
            set => _db[key] = value;
        }

        public void Delete(Keccak key)
        {
            if (_db.ContainsKey(key))
            {
                _db.Remove(key);
            }
        }

        public void Print(Action<string> output)
        {
            foreach (KeyValuePair<Keccak, byte[]> keyValuePair in _db)
            {
                Node node = PatriciaTree.RlpDecode(new Rlp(keyValuePair.Value));
                output($"{keyValuePair.Key.ToString(true).Substring(0, 6)} : {node}");
            }
        }

        public InMemoryDb TakeSnapshot()
        {
            return new InMemoryDb(_db);
        }
    }
}