using System.Collections.Generic;

namespace Nethermind.Store
{
    public interface IFullDb : IDb
    {
        ICollection<byte[]> Keys { get; }

        ICollection<byte[]> Values { get; }
        
        void Remove(byte[] key);
    }
}