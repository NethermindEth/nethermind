using Nevermind.Core.Sugar;

namespace Nevermind.Store
{
    internal class LeafNode : Node
    {
        public LeafNode()
        {
        }

        public LeafNode(byte[] key, byte[] value)
        {
            Key = key;
            Value = value;
        }

        public byte[] Key { get; set; }
        public byte[] Value { get; set; }

        public override string ToString()
        {
            return $"[{Key.ToHex(false)}, {Value.ToHex()}";
        }
    }
}