using System;
using System.Collections.Generic;
using System.Text;
using Nethermind.Store;
using RocksDbSharp;

namespace Nethermind.Db
{
    public class FullDbOnTheRocks : DbOnTheRocks, IFullDb
    {       
        public FullDbOnTheRocks(string dbPath) : base(dbPath)
        {
        }

        public ICollection<byte[]> Keys
        {
            get { return GetKeysOrValues(x => x.Key()); }
        }

        public ICollection<byte[]> Values
        {
            get { return GetKeysOrValues(x => x.Value()); }
        }

        private ICollection<byte[]> GetKeysOrValues(Func<Iterator, byte[]> selector)
        {
            ReadOptions readOptions = new ReadOptions();
            List<byte[]> items = new List<byte[]>();            
            using (Iterator iter = _db.NewIterator(readOptions: readOptions))
            {
                iter.SeekToFirst();
                while (iter.Valid())
                {
                    byte[] item = selector.Invoke(iter);
                    items.Add(item);
                    iter.Next();
                }
            }

            return items;
        }
    }
}