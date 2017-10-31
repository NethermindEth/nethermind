using System;
using System.Linq;
using Nevermind.Core.Sugar;

namespace Nevermind.Store
{
    internal class BranchNode : Node
    {
        public BranchNode()
            : this(new KeccakOrRlp[16], new byte[0])
        {
        }

        public BranchNode(KeccakOrRlp[] nodes, byte[] value)
        {
            Value = value ?? throw new ArgumentNullException(nameof(value));
            Nodes = nodes ?? throw new ArgumentNullException(nameof(nodes));

            if (nodes.Length != 16)
            {
                throw new ArgumentException($"{nameof(BranchNode)} should have 16 child nodes", nameof(nodes));
            }
        }

        public KeccakOrRlp[] Nodes { get; set; }

        private byte[] _value;

        public byte[] Value
        {
            get => _value;
            set => _value = value ?? throw new ArgumentNullException(nameof(value));
        }

        public bool IsValid => (Value.Length > 0 ? 1 : 0) + Nodes.Count(n => n != null) > 1;

        public override string ToString()
        {
            return $"[{string.Join(",", Nodes.Select(n => n?.ToString() ?? "<>"))}, {Value.ToHex(false)}]";
        }
    }
}