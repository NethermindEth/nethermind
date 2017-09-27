using System.Diagnostics;
using Nevermind.Core.Encoding;

namespace Nevermind.Store
{
    internal class ExtensionNode : Node
    {
        public ExtensionNode()
        {
        }

        [DebuggerStepThrough]
        public ExtensionNode(HexPrefix key, KeccakOrRlp nextNode)
        {
            Key = key;
            NextNode = nextNode;
        }

        public byte[] Path => Key.Path;
        public HexPrefix Key { get; set; }
        public KeccakOrRlp NextNode { get; set; }

        public override string ToString()
        {
            return $"[{Key}, {NextNode}]";
        }
    }
}