using System.Linq;
using Nevermind.Core.Encoding;
using Nevermind.Core.Sugar;

namespace Nevermind.Store
{
    internal class BranchNode : Node
    {
        public BranchNode()
        {
            Nodes = new KeccakOrRlp[16];
        }

        public BranchNode(KeccakOrRlp[] nodes, byte[] value)
        {
            Value = value;
            Nodes = nodes;
        }

        public KeccakOrRlp[] Nodes { get; set; }
        public byte[] Value { get; set; }

        public override string ToString()
        {
            return $"[{string.Join(",", Nodes.Select(n => n?.ToString() ?? "<>"))}, {Value.ToHex(false)}";
        }
    }
}