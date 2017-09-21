using Nevermind.Core.Encoding;
using Nevermind.Core.Sugar;

namespace Nevermind.Store
{
    public class ExtensionNode : Node
    {
        public ExtensionNode()
        {
        }

        public ExtensionNode(byte[] key, Keccak nextNodeHash)
        {
            Key = key;
            NextNodeHash = nextNodeHash;
        }

        public byte[] Key { get; set; }
        public Keccak NextNodeHash { get; set; }

        public override string ToString()
        {
            return $"[{Key.ToHex(false)}, {NextNodeHash.ToString(true)}";
        }
    }
}