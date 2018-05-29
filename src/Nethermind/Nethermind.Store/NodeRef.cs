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

using System.Diagnostics;
using Nethermind.Core.Encoding;

namespace Nethermind.Store
{
    [DebuggerDisplay("KeccakOrRlp={KeccakOrRlp}, Node={Node}")]
    internal class NodeRef
    {
        public NodeRef(KeccakOrRlp keccakOrRlp, bool isRoot = false)
        {
            KeccakOrRlp = keccakOrRlp;
            IsRoot = isRoot;
        }

        public NodeRef(Node node, bool isRoot = false)
        {
            Node = node;
            IsRoot = isRoot;
        }

        public void ResolveNode(PatriciaTree tree)
        {
            if (Node == null)
            {
                Node = tree.GetNode(KeccakOrRlp);
            }
        }

        public void ResolveKey()
        {
            if (KeccakOrRlp == null)
            {
                _fullRlp = PatriciaTree.RlpEncode(Node);
                KeccakOrRlp = new KeccakOrRlp(_fullRlp);
            }
        }

        public KeccakOrRlp KeccakOrRlp { get; set; }
        private Rlp _fullRlp;
        public Rlp FullRlp => KeccakOrRlp.IsKeccak ? _fullRlp : KeccakOrRlp.GetOrEncodeRlp();
        
        public Node Node { get; set; }
        public bool IsRoot { get; set; }

        public bool IsDirty
        {
            get
            {
                if (Node == null)
                {
                    return false;
                }

                return Node.IsDirty;
            }
        }
    }
}