using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Synchronization
{
    public class RequestItem
    {
        public RequestItem(Keccak hash, NodeDataType nodeType, int level, int priority)
        {
            Hash = hash;
            NodeDataType = nodeType;
            Level = level;
            Priority = priority;
        }
            
        public Keccak Hash { get; set; }
            
        public NodeDataType NodeDataType { get; set; }
            
        public int Level { get; set; }
            
        public int Priority { get; set; }
    }
}