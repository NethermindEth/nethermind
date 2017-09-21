using Nevermind.Core.Sugar;

namespace Nevermind.Store
{
    internal class ExtensionNode : Node
    {
        public ExtensionNode()
        {
        }

        public ExtensionNode(byte[] key, KeccakOrRlp nextNode)
        {
            Key = key;
            NextNode = nextNode;
        }

        public byte[] Key { get; set; }
        public KeccakOrRlp NextNode { get; set; }

        public override string ToString()
        {
            return $"[{Key.ToHex(false)}, {NextNode}";
        }
    }
}