//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System.Diagnostics;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Enr
{
    public abstract class EnrContentEntry
    {
        /// <summary>
        /// A key string of the node record entry.
        /// </summary>
        public abstract string Key { get; }

        internal int GetRlpLength()
        {
            return Rlp.LengthOf(Key) + GetRlpLengthOfValue();
        }

        /// <summary>
        /// Needed for optimized RLP serialization.
        /// </summary>
        /// <returns></returns>
        protected abstract int GetRlpLengthOfValue();

        /// <summary>
        /// Encodes the entry into an RLP stream. 
        /// </summary>
        public void Encode(RlpStream rlpStream)
        {
            rlpStream.Encode(Key);
            EncodeValue(rlpStream);
        }

        protected abstract void EncodeValue(RlpStream rlpStream);

        public override int GetHashCode()
        {
            return Key.GetHashCode();
        }
    }

    /// <summary>
    /// Single key, value pair entry in the ENR record content.
    /// </summary>
    [DebuggerDisplay("{Key} {Value}")]
    public abstract class EnrContentEntry<TValue> : EnrContentEntry
    {
        /// <summary>
        /// A value of the node record entry.
        /// </summary>
        public TValue Value { get; }

        protected EnrContentEntry(TValue value)
        {
            Value = value;
        }

        public override string ToString()
        {
            return $"{Key} {Value}";
        }
    }
}
