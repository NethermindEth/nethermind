using System;
using System.Collections.Generic;
using Nevermind.Core.Encoding;

namespace Nevermind.Store
{
    public class Db
    {
        private readonly Dictionary<Keccak, byte[]> _db = new Dictionary<Keccak, byte[]>();

        public byte[] this[Keccak key]
        {
            get => _db[key];
            set => _db[key] = value;
        }

        public void Delete(Keccak key)
        {
            _db.Remove(key);
        }

        // temp
        public int Count => _db.Count;

        public void Print(Action<string> output)
        {
            foreach (KeyValuePair<Keccak, byte[]> keyValuePair in _db)
            {
                Node node = PatriciaTree.RlpDecode(new Rlp(keyValuePair.Value));
                output($"{keyValuePair.Key.ToString(true).Substring(0, 6)} : {node}");
            }
        }
    }
}