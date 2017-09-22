using System.Diagnostics;
using Nevermind.Core.Encoding;
using Nevermind.Core.Sugar;

namespace Nevermind.Store
{
    internal class LeafNode : Node
    {
        public LeafNode()
        {
        }

        [DebuggerStepThrough]
        public LeafNode(HexPrefix key, byte[] value)
        {
            Key = key;
            Value = value;
        }

        public byte[] Path => Key.Path;
        public HexPrefix Key { get; set; }
        public byte[] Value { get; set; }

        public override string ToString()
        {
            return $"[{Key}, {Value.ToHex()}";
        }
    }
}