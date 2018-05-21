/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Linq;
using Nethermind.Core.Extensions;

namespace Nethermind.Store
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

        public KeccakOrRlp[] Nodes { get; private set; }

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