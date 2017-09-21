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

        // temp
        public int Count => _db.Count;
    }
}