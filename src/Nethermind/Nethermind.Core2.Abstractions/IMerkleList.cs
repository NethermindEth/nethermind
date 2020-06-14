using System.Collections.Generic;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.Core2
{
    public interface IMerkleList
    {
        Root Root { get; }
        
        uint Count { get; }
        
        void Insert(Bytes32 leaf);
        
        IList<Bytes32> GetProof(in uint index);
    }
}