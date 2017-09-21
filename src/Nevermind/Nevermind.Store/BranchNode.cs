using System.Linq;
using Nevermind.Core.Encoding;
using Nevermind.Core.Sugar;

namespace Nevermind.Store
{
    public class BranchNode : Node
    {
        public BranchNode()
        {
            Nodes = new Keccak[16];
        }

        public BranchNode(Keccak[] nodes, byte[] value)
        {
            Value = value;
            Nodes = nodes;
        }

        public Keccak[] Nodes { get; set; }
        public byte[] Value { get; set; }

        public override string ToString()
        {
            return $"[{string.Join(",", Nodes.Select(n => n == null ? "<>" : n.ToString(true)))}, {Value.ToHex(false)}";
        }
    }
}