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
using Nethermind.Core.Crypto;

namespace Nethermind.Core.Model
{
    public class NodeId : IEquatable<NodeId>, IEquatable<PublicKey>
    {
        public NodeId(PublicKey publicKey)
        {       
            PublicKey = publicKey ?? throw new ArgumentNullException(nameof(publicKey));
        }

        public PublicKey PublicKey { get; }

        public byte[] Bytes => PublicKey.Bytes;

        public bool Equals(NodeId other)
        {
            if (other == null)
            {
                return false;
            }

            return PublicKey.Equals(other.PublicKey);
        }

        public bool Equals(PublicKey other)
        {
            if (other == null)
            {
                return false;
            }

            return PublicKey.Equals(other);
        }

        public override string ToString()
        {
            return PublicKey.ToShortString();
        }

        public string ToFullString()
        {
            return base.ToString();
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as NodeId);
        }

        public override int GetHashCode()
        {
            return PublicKey.GetHashCode();
        }
        
        public static bool operator ==(NodeId a, NodeId b)
        {
            if (ReferenceEquals(a, b))
            {
                return true;
            }

            return a?.Equals(b) ?? false;
        }

        public static bool operator !=(NodeId a, NodeId b)
        {
            return !(a == b);
        }
    }
}